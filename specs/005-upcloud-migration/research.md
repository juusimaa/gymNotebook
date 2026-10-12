# Research: Move Hosting from Azure to UpCloud

Facts come from Subscription Tracker's live UpCloud setup
(`docker-subscription-tracker/specs/001-upcloud-migration/research.md`, "tracker
R*" below) and this repository. Items marked **(gate)** are measured in G1/G2 of
[plan.md](plan.md) before anything changes; items marked **(verify)** are
checked during provisioning and written back here.

## R1. Capacity on the shared server

- **Decision**: co-host on the tracker's Starter `1xCPU-2GB` server, with a hard
  memory and CPU cap on Gym Notebook's containers so a leak or a heavy request
  can only hurt Gym Notebook.

  | Container | `mem_limit` | `cpus` | Why |
  |---|---|---|---|
  | `gym-api` | 512 MB | 0.75 | .NET sizes its GC heap from the container limit (75% by default), so the cap is respected without a separate heap setting. A 25 MB restore file (D9 of spec 004) becomes several times that as objects; 384 MB felt too tight to promise. 0.75 CPU leaves the tracker a quarter of the core during an export. |
  | `gym-web` | 64 MB | 0.25 | nginx serving static files. |

- **Rationale**: the tracker's containers have no limits. Without caps on ours,
  the kernel's OOM killer picks a victim host-wide, and that could be the
  tracker's API. With caps, Gym Notebook's container is killed inside its own
  cgroup and restarted by Docker.
- **Budget**: Gym Notebook may use at most ~576 MB. That fits if the tracker
  stack plus the OS uses under ~900 MB, leaving the 20% the existing memory
  alert expects.
- **Measured (G1, owner, 2026-10-11): pass.** Command in
  [quickstart § G1](quickstart.md#g1-capacity-gate-before-any-change).

  | Check | Pass rule | Measured |
  |---|---|---|
  | CPUs | — | 1 |
  | Memory `available` | ≥ 900 MB | 1256 MB of 1855 MB (used 598, no swap) |
  | Disk free | ≥ 5 GB | 14 GB of 20 GB (5.2 GB used) |
  | Load average | < 0.5 | 0.01 / 0.03 / 0.02 |
  | Tracker containers | — | backend 80 MB, caddy 13 MB, frontend 3 MB; CPU < 1% |

  With Gym Notebook at its full 576 MB cap, about 680 MB (≈37%) would remain
  available, above the 30% idle target that G2 measures (SC-006).
  A fail would have meant stopping to decide between Premium/Starter 4 GB or
  dropping the plan.
- **G2 (gate, after rehearsal deploy)**: with both stacks idle, `available` is
  ≥ 30% of memory (SC-006). Then restore the 100,000-set reference backup and
  run its export through `api.gymnotebook.fit`; `gym-api` must not be
  OOM-killed (`docker inspect -f '{{.State.OOMKilled}}'`), memory stays below
  80%, and tracker `/health` answers under 1 s throughout.
  **Passed 2026-10-12 (T044)**: before the load `available` was 59%. Under
  the load (export 2.1 s, restore 27.3 s, export 1.3 s) memory peaked at 57%
  used, the tracker's slowest `/health` was 0.12 s, and `OOMKilled` stayed
  `false`. Idle afterwards: at least 48% `available`. The figure was read as
  48%, and taking it as either `used` or `available` still clears 30%.
- **Disk**: tracker used 3.2 GB of 20 GB at first start (tracker R1). The two
  Gym Notebook images add under 1 GB. The tracker's weekly
  `image-prune.service` runs `docker image prune -a` host-wide, so it already
  covers our images.

## R2. Sharing the managed PostgreSQL service

- **Decision**: a separate database `gymnotebook` owned by a separate role
  `gymnotebook`, created by the admin user in the same service.
  - `REVOKE CONNECT ON DATABASE gymnotebook FROM PUBLIC; GRANT CONNECT … TO gymnotebook;`
    PostgreSQL grants `CONNECT` to `PUBLIC` by default, so without this any
    role could connect to any database. The same is done for the tracker's
    database so ours can't connect there (FR-004).
  - The database is owned by `gymnotebook`. In PostgreSQL 15+ the `public`
    schema belongs to `pg_database_owner`, so EF migrations can create tables
    with no further grants.
- **Gate (G1): the tracker connects as `upadmin`.** Measured 2026-10-11: five
  idle `upadmin` connections to `defaultdb` from the server's private address
  `10.0.0.3` (matching the tracker's `pool_size=5`), and no other application
  role. The tracker's data lives in `defaultdb`. Because `upadmin` can read
  every database, FR-004 cannot hold, so a tracker-only role is **required**
  first, in the tracker repo (plan § Tracker repository changes, item 5).
- **Connections**: `max_connections` is 50 (tracker R2). The tracker's pool is
  `pool_size=5, max_overflow=10`, at most 15. Npgsql's default
  `Maximum Pool Size` is 100, so ours is set to **10** in the connection string.
  Together 25, leaving room for the admin, backups, `pg_dump` and psql.
  G1 (2026-10-11): 20 rows in `pg_stat_activity`, including service background
  processes; 20 + our 10 = 30 ≤ 40, a pass.
  T025 (2026-10-12): with 40 parallel `/health` callers, `gymnotebook` peaked
  at 10 connections, never more, and the tracker's `/health` answered 200 in
  about 0.1 s throughout.
- **TLS**: as the tracker (tracker R4): private network only, non-default port,
  `SSL Mode=VerifyFull;Root Certificate=/etc/gymnotebook/upcloud-ca.pem`.
- **Storage**: 10 GiB shared. G1 (2026-10-11): tracker `defaultdb` 8.7 MB;
  the service's other databases (`_updb`, templates) about 8 MB each. Neon
  7968 kB. Tracker + Neon ≈ 17 MB, far under 8 GiB, a pass.
- **Backups**: the service's daily backups (3 days, tracker R3) cover both
  databases. UpCloud restores a whole service into a **new** service, so a
  Gym Notebook-only restore is: restore into a new service, `pg_dump` the
  `gymnotebook` database from it, `pg_restore --clean` into the live one with
  `gym-api` stopped, delete the temporary service. The tracker's restore took
  about 17 minutes (tracker T038). The temporary service bills hourly while it
  exists: cents, and only during a restore.
- **Verified 2026-10-11 (T002)**: `upadmin` via psql on the Developer plan
  can `CREATE ROLE … LOGIN` and `CREATE DATABASE … OWNER` that role; a
  throwaway `t002_probe` role and database were created and dropped again.
- **Verified 2026-10-11 (before T019)**: `upadmin` can also
  `GRANT pg_read_all_stats` (probe role created, granted, dropped), so the
  tracker's size alert can read `gymnotebook`'s size without `CONNECT` on it.

## R3. PostgreSQL 18

- **Decision**: the managed service runs 18, so tests, Compose and the
  constitution move from 17 to 18 (FR-009).
- `postgres:18` changed `PGDATA` to `/var/lib/postgresql/18/docker` and refuses
  to start with a volume at `/var/lib/postgresql/data`. Compose mounts the
  volume at `/var/lib/postgresql` instead, under a **new** volume name
  (`db_data_18`) so the old 17 cluster is never opened by 18. Local data in the
  old volume is not migrated (dev data; take an app backup first if wanted).
- The copy from Neon uses `pg_dump` from the `postgres:18` image, which can dump
  both 17 and 18 servers. G1 (2026-10-11): Neon runs PostgreSQL 17.11
  (aarch64).
- Sources: https://hub.docker.com/_/postgres (PGDATA change in 18),
  https://www.postgresql.org/docs/18/app-pgdump.html

## R4. One edge for two apps

- **Decision**: the tracker's Caddy stays the only process on ports 80/443.
  Its Caddyfile gains one line, `import /etc/caddy/sites/*.caddy`, and a
  read-only mount of `/opt/edge/sites`. Gym Notebook owns its site file
  (`deploy/gymnotebook.caddy` here), copied into `/opt/edge/sites/` by the
  provisioning runbook. Caddy joins an external Docker network `edge`; Gym
  Notebook's two containers join it too.
- **Rationale**:
  - The edge stays owned by one repository (FR-005), and Gym Notebook's
    hostnames and routes live in Gym Notebook's repository.
  - A Gym Notebook deploy never touches Caddy. Only an admin reload does, and
    `caddy reload` validates the whole config first and keeps the old one if it
    fails, so a broken site file cannot take the tracker down.
  - Caddy resolves upstream names when it dials, so a stopped `gym-api` gives
    502 on Gym Notebook's hostnames only.
- **Name clash (important)**: Docker registers each service's name as an alias
  on every network it joins. If our services were called `backend` and
  `frontend` like the tracker's, Caddy's `reverse_proxy backend:8000` could
  resolve to our container over `edge`. Our services are therefore named
  `gym-api` and `gym-web`, and only Caddy from the tracker joins `edge`.
- **Alternatives**: (a) add our site blocks to the tracker's Caddyfile:
  simplest, but every Gym Notebook hostname or header change becomes a tracker
  PR. (b) a separate edge repository: cleanest, more moving parts than two
  apps justify.
- **Verified 2026-10-11 (T015)** on `caddy:2.11.7` with `caddy validate`: an
  `import` glob that matches nothing (empty or missing `/etc/caddy/sites`)
  exits 0 with one warning, `No files matching import glob pattern`, so the
  tracker change can merge before our file exists. A valid site file is
  included; a broken one fails validation (exit 1), which is what makes
  `caddy reload` keep the old config.

## R5. Real client address

- Caddy with no `trusted_proxies` **replaces** the client's `X-Forwarded-For`
  with the TCP peer (tracker R5, pinned `caddy:2.11.7`). The API's existing
  `FORWARDED_HEADERS_ENABLED=true` path takes the rightmost entry with
  `ForwardLimit = 1` and cleared known-proxy lists (PLAN.md § rate limiting),
  so it gets the real client. No code change; only the comment that names
  Container Apps changes.
- `gym-api` publishes no port, so the only way in is through Caddy (FR-014).
  The flag is set in `compose.prod.yml`, not the env file, because it is only
  true of this stack.

## R6. Deploy

- **Decision**: copy the tracker's model (tracker `deploy/bin/deploy.sh`,
  `contracts/deploy-interface.md`) with a separate Linux user `gymdeploy` and
  key. The key's `authorized_keys` line is
  `restrict,command="/opt/gymnotebook/bin/deploy.sh"`. The script accepts only
  our tag format `main-<40 hex>` (what `build-and-push.yml` publishes), writes
  it to `/var/lib/gymnotebook/image-tag.env`, runs `up -d --wait` for the
  `gymnotebook` Compose project only, checks `https://api.gymnotebook.fit/health`
  through Caddy with `--resolve` to 127.0.0.1, and rolls back on failure.
- **Isolation, honestly stated**: `gymdeploy` is in the `docker` group, which is
  root-equivalent. The boundary between the two deploy keys is the forced
  command, not Unix permissions. That's the same trust the tracker's key
  already has.
- CI: a `deploy` job after `build-and-push`, gated by a repository variable
  `UPCLOUD_DEPLOY_ENABLED` like the tracker's, with the known host pinned.
  The Azure workflow stays until cutover. GitHub secrets shrink to the deploy
  key, host and known-hosts line: app secrets now live only on the server.

## R7. Health checks without new packages

- The `aspnet` runtime image has no `curl` or `wget`. Compose's API health
  check uses bash's `/dev/tcp` to send `GET /health` and checks for ` 200 `.
  It needs no new package and no second .NET process every 10 s.
- `/health` already runs a database check, so "healthy" includes the database.
- The frontend check uses busybox `wget`, as the tracker's does.

## R8. Where the secrets come from

- `Jwt__Secret`, `RESEND_API_KEY` and `TURNSTILE_SECRET_KEY` are GitHub secrets,
  which cannot be read back. The current values come from the Azure Container
  App's secrets (`az containerapp secret list --show-values`) during
  provisioning, so sessions and emailed links keep working (FR-010).
- They go into `/etc/gymnotebook/.env`, `root:gymdeploy 0640`. The tracker's
  `deploy` user is not in `gymdeploy`.

## R9. Copying the data

- From the server, in `postgres:18`: `pg_dump -Fc --no-owner --no-privileges`
  over Neon's **unpooled** endpoint with `sslmode=require`;
  `pg_restore --no-owner --no-privileges --exit-on-error` as `gymnotebook` over
  the private network; `ANALYZE`. Same steps as tracker R11.
- Freeze: the owner stops logging, then `ALTER DATABASE … SET
  default_transaction_read_only = on` on Neon. This keeps Neon an exact
  rollback copy.
- Check: `deploy/bin/verify-copy.sql` (row count per table, plus
  `__EFMigrationsHistory`'s last migration) on both sides; outputs must be
  identical. The rehearsal copy was meant to measure the time; it was skipped
  (2026-10-12) because with one user the freeze length does not matter. The
  cutover copy is the first one.

## R10. Cost

| Item | Before | After |
|---|---|---|
| UpCloud server + database (tracker's) | €15.40 | €15.40 |
| Azure Container Apps + Log Analytics (this app) | scale-to-zero, small but non-zero | €0 |
| Neon | Free | deleted |
| UptimeRobot, Resend, Cloudflare | Free | Free |

No new billed resource. The only possible extra is a temporary restore service
(R2), billed hourly only while a restore runs.

## R11. Privacy and logs

- Data moves from Azure (Sweden) and Neon/AWS (Frankfurt) to UpCloud (Helsinki,
  Finland). UpCloud becomes a processor for this app; reuse the tracker's
  UpCloud agreement review if one exists, otherwise review its DPA (FR-023).
- **Logs change shape**: Azure kept container logs about 30 days. On the server,
  Docker's `json-file` keeps at most 3 × 10 MB per container, so retention is
  size-bounded, not time-bounded.
- **Edge access log**: the tracker's Caddy logs every request on its own
  hostnames. Our site file sets **no** `log` directive, keeping the decision
  that `gym-web` has no access log (`frontend/nginx.conf`). Caddy still logs
  errors, without request bodies.
- `docs/privacy/processing-decision.md` names Neon in its signed P1 and P2
  "Where it lives" lines, so the location change needs an owner-signed
  amendment before cutover.
