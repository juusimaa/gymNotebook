---

description: "Task list for moving Gym Notebook's hosting from Azure + Neon to UpCloud"
---

# Tasks: Move Hosting from Azure to UpCloud

**Input**: [spec.md](spec.md), [plan.md](plan.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Status**: Draft for owner review (Principle VII). AI implements by default; the owner names any task they will write themselves. Tasks marked **(owner)** need the server, the Azure/Neon/UpCloud consoles, DNS or a signature, and are run by the owner.

**Tests**: The plan asks for CI checks on the deploy pieces (`docker compose config`, `caddy validate`, the deploy-guard shell test) and the existing xUnit suite on `postgres:18`. Edge, isolation and cutover behavior are checked by the owner's quickstart scenarios, since there is no staging server. No app code changes, so no new xUnit or Vitest tests.

**Organization**: Phases follow the plan's delivery sequence (PR 1 → 2 → tracker PR → 3 → rehearsal → 4 → cutover → 5), because the stories share files and the cutover (US1) needs the isolation (US2) and deploy (US3) checks to pass first. Each task carries its story label and the PR it ships in.

**Repositories**: paths are relative to this repository unless they start with `docker-subscription-tracker/` (the sibling checkout at `../docker-subscription-tracker`).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different files, no dependency on an unfinished task)
- **[Story]**: US1 data intact, US2 tracker unaffected, US3 merges deploy themselves, US4 Azure and Neon gone

---

## Phase 1: Setup (gates before any change)

**Purpose**: confirm the shared resources fit before anything is written or moved (FR-002).

- [x] T001 (owner) Gate G1: measure server memory, disk, load, database connections, sizes and Neon's version per quickstart § G1; record in `specs/005-upcloud-migration/research.md` R1/R2. **Passed 2026-10-11**; found the tracker connecting as `upadmin`, so the tracker-only role (T014) is required.
- [x] T002 (owner) Confirm that `upadmin` can `CREATE ROLE` and `CREATE DATABASE` on the Developer plan (research R2 "(verify)") with a throwaway role/database that is dropped again; write the result into `specs/005-upcloud-migration/research.md` R2. Stop and re-plan if it cannot. **Passed 2026-10-11**.

**Checkpoint**: G1 passed and role creation confirmed. Nothing in production has changed.

---

## Phase 2: Foundational (blocking prerequisites)

**Purpose**: Postgres 18 everywhere (PR 1) and the shared `deploy/` skeleton (PR 2) that every story builds on.

**⚠️ CRITICAL**: no story work starts until PR 1 is merged; the PR 2 files below land in the same PR as the US1/US2/US3 runbook and script tasks marked "(PR 2)".

### PR 1 — Postgres 18

- [x] T003 [P] Change the Testcontainers image from `postgres:17` to `postgres:18` in `backend/GymNotebook.Tests/GymNotebookFactory.cs` (line 33).
- [x] T004 [P] Change the Testcontainers image from `postgres:17` to `postgres:18` in `backend/GymNotebook.Tests/TwoHostGymNotebookFixture.cs` (line 33).
- [x] T005 [P] In `docker-compose.yml`: `db` image `postgres:18`; volume renamed `db_data` → `db_data_18` and mounted at `/var/lib/postgresql` (not `/var/lib/postgresql/data`), with a comment that `postgres:18` moved `PGDATA` to `/var/lib/postgresql/18/docker`, refuses the old mount path, and the new name keeps 18 from ever opening the 17 cluster; old dev data is not migrated (plan D5, research R3). Update the top-level `volumes:` key to match.
- [x] T006 [P] Amend `.specify/memory/constitution.md`: Principle I "PostgreSQL 17" → 18; Principle IV's tests-match-production rule names 18; Principle V's production-secrets sentence (line 96, "Container Apps secrets through `secretref`") → `/etc/gymnotebook/.env`, `root:gymdeploy 0640`, never in images or the repository. Bump the version (minor), update **Last Amended**, and add the sync-impact note (plan § Complexity Tracking).
- [x] T007 [P] Update the Postgres version text in `README.md` (stack table line 20 → "PostgreSQL 18" keeping "Neon in production" until PR 5; the `docker run … postgres:17` line 56; the Testcontainers sentence line 161), `PLAN.md` § Stack and § Testing and CI (the `postgres:17` tag sentence, line 332), and `AGENTS.md` (stack line 34). Leave historical milestone text and `docs/privacy/release-checklist.md` evidence as they are.
- [x] T008 Verify PR 1: `dotnet format backend/GymNotebook.sln --verify-no-changes`, `dotnet test backend/GymNotebook.sln` (all pass on `postgres:18`), and `docker compose up --build` on a fresh volume serves `/health` → 200 after migrations. Record counts in this file under the phase.

**PR 1 verified 2026-10-11**: `dotnet format --verify-no-changes` clean; `dotnet test` 463 passed, 0 failed on `postgres:18`; `docker compose up --build` on a fresh `db_data_18` volume ran PostgreSQL 18.6 with the cluster in `/var/lib/postgresql/18/docker`, applied all 9 migrations and served `/health` → 200. Constitution amended to 3.1.0.

### PR 2 — shared `deploy/` files

- [x] T009 Create `deploy/compose.prod.yml` (Compose project `gymnotebook`), starting with a comment saying what the file is for. Services exactly as data-model § Containers: `gym-api` (`ghcr.io/juusimaa/gymnotebook-backend:${IMAGE_TAG}`) and `gym-web` (`ghcr.io/juusimaa/gymnotebook-frontend:${IMAGE_TAG}`), `IMAGE_TAG` read from `/var/lib/gymnotebook/image-tag.env`; networks `default` + external `edge`; **no `ports:`**; `restart: unless-stopped`; `gym-api` `mem_limit: 512m`, `cpus: 0.75`; `gym-web` `mem_limit: 64m`, `cpus: 0.25`, each with the reason from research R1 in a comment. `gym-api`: `env_file: /etc/gymnotebook/.env`, `FORWARDED_HEADERS_ENABLED: "true"` set here with the R5 reason, `/etc/gymnotebook/upcloud-ca.pem` mounted read-only, healthcheck `GET /health` via bash `/dev/tcp` checking for ` 200 ` (R7). `gym-web`: `API_URL: https://api.gymnotebook.fit`, `TURNSTILE_SITE_KEY` from the env file, healthcheck with busybox `wget` (R7). `json-file` logging capped at `max-size: 10m`, `max-file: 3` (R11). Service names must stay `gym-api`/`gym-web`, never `backend`/`frontend`, with the D2 name-clash comment.
- [x] T010 [P] Create `deploy/gymnotebook.caddy` per `contracts/edge-http.md`: `gymnotebook.fit` → `reverse_proxy gym-web:80`, `api.gymnotebook.fit` → `reverse_proxy gym-api:8080`; no `trusted_proxies` (so Caddy replaces client `X-Forwarded-For`, R5); **no `log` directive**, with the D10 comment pointing at `frontend/nginx.conf`'s `access_log off`; no maintenance page or `/health` special case.
- [x] T011 [P] Create `deploy/tests/sample.env` with fake values for every variable in `contracts/server-env.md` § `gym-api` plus `TURNSTILE_SITE_KEY`, and a header comment that the values are fake and only for `docker compose config`. Diff `infra/modules/container-app-api.bicep` env list against the contract table and add any variable the contract misses (e.g. `ASPNETCORE_ENVIRONMENT` default, `Jwt__RenewalCapHours`, `EMAIL_DAILY_CAP` if Bicep sets them); update `contracts/server-env.md` in the same PR if it changes.
- [x] T012 Add a `deploy-config` job to `.github/workflows/test.yml` modelled on `docker-subscription-tracker/.github/workflows/test.yml` (job `deploy-config`): `docker network create edge` if needed, `docker compose -f deploy/compose.prod.yml --env-file deploy/tests/sample.env config -q` (with the `env_file`/`image-tag.env` paths satisfied by sample files), and `caddy:2.11.7 caddy validate` on a small test Caddyfile `deploy/tests/Caddyfile` that sets `local_certs` and imports `deploy/gymnotebook.caddy`. Runs on every push and PR (FR-024).
- [x] T013 Create `deploy/README.md` skeleton: what the directory is, the file list, the server layout table from data-model § Server, and the environment section listing every variable from `contracts/server-env.md` with where it comes from (never the values). The provision, deploy, cutover, rollback and restore sections are filled by T020, T021, T030 and T038–T041.

**Checkpoint**: PR 1 merged; the `deploy/` files exist and pass `deploy-config` in CI.

---

## Phase 3: User Story 2 — Subscription Tracker is not affected (Priority: P1)

**Goal**: Gym Notebook runs beside the tracker with its own capped containers, its own database and role, its own secrets, and the tracker's Caddy serving both through a site file this repository owns.

**Independent Test**: quickstart § Provision isolation checks plus Rehearsal steps 2, 3, 5, 6: a stopped or OOM-ing `gym-api` gives 502 on our hosts only; the tracker's `/health` stays under 1 s and green on UptimeRobot; neither database role can connect to the other's database; a forged `X-Forwarded-For` does not change the rate-limit bucket.

### Tracker repository PR (`docker-subscription-tracker`, "PR T")

- [x] T014 [P] [US2] Add a tracker-only login role that owns `defaultdb` and switch the tracker's connection string to it, with the runbook steps in `docker-subscription-tracker/deploy/README.md` (create role, `ALTER DATABASE defaultdb OWNER TO …`, reassign table ownership, `REVOKE CONNECT ON DATABASE defaultdb FROM PUBLIC`, grant `CONNECT` to the new role, then update the tracker's server env file and restart its backend only). Applied on the server before anything else in this phase (G1 finding; FR-004). **Applied 2026-10-12** (see T019).
- [ ] T015 [P] [US2] Add `import /etc/caddy/sites/*.caddy` with an explanatory comment to `docker-subscription-tracker/deploy/Caddyfile`; confirm research R4's "(verify)" that a glob matching nothing only logs a warning, and record it in this repo's `specs/005-upcloud-migration/research.md` R4.
- [ ] T016 [P] [US2] In `docker-subscription-tracker/deploy/compose.prod.yml`: mount `/opt/edge/sites:/etc/caddy/sites:ro` on `caddy`, and join `caddy` (only) to the external network `edge` as well as `default`; extend `docker-subscription-tracker/deploy/tests/test_compose_prod.py` and the edge test in `docker-subscription-tracker/deploy/tests/edge/` to cover both.
- [ ] T017 [P] [US2] In `docker-subscription-tracker/deploy/bin/ops-check.py`: database-size check uses `sum(pg_database_size(datname))` over the service's databases (FR-019) and `gymnotebook.fit`, `api.gymnotebook.fit` join the certificate-expiry hosts (D9); grant `pg_read_all_stats` to the check's role or add a second read-only URL (plan § Tracker repository changes, item 3); extend `docker-subscription-tracker/deploy/tests/test_ops_check.py`.
- [ ] T018 [US2] Add "create the `edge` network before `up`" and "create `/opt/edge/sites`" to § Provision in `docker-subscription-tracker/deploy/README.md`; run that repo's tests; open the tracker PR.
- [x] T019 [US2] (owner) Merge the tracker PR and apply it on the server: tracker role first (T014), then `docker network create edge`, `/opt/edge/sites`, Caddy recreated with the new mount/network. Check `subscriptionstrack.com` and its API stay green on UptimeRobot throughout. **2026-10-12**: edge applied (network, `/opt/edge/sites`, Caddy imports our site), but the role step (T014) was not: no `tracker` role, `defaultdb` owned by `upadmin` with `=Tc` (CONNECT for `PUBLIC`). **Role applied the same day**, with two runbook gaps found on the live service: UpCloud creates `public` in `defaultdb` owned by `upadmin`, so the handover also needs `ALTER SCHEMA public OWNER TO tracker` (without it `ALTER TABLE … OWNER` fails with `permission denied for schema public`); and `defaultdb` held a stray `__EFMigrationsHistory` (one row, Gym Notebook's empty `InitialCreate`; its next migration had failed on the tracker's existing `users` table, so nothing else was created), dropped in the same transaction. The tracker now connects as `tracker`; its `/health` and the hourly ops check pass.

### Gym Notebook provisioning runbook (PR 2)

- [x] T020 [US2] Write § Provision in `deploy/README.md`: (1) as `upadmin` over the private network in a `postgres:18` container: `CREATE ROLE gymnotebook LOGIN PASSWORD …` (no superuser, no `CREATEDB`/`CREATEROLE`), `CREATE DATABASE gymnotebook OWNER gymnotebook`, `REVOKE CONNECT ON DATABASE gymnotebook FROM PUBLIC`, `GRANT CONNECT ON DATABASE gymnotebook TO gymnotebook` (research R2, data-model § Database service); (2) create `/opt/gymnotebook/` (`root:root 0755`) and copy `deploy/compose.prod.yml` and `deploy/bin/` there; (3) create group `gymdeploy`, `/etc/gymnotebook/.env` and `/etc/gymnotebook/upcloud-ca.pem` as `root:gymdeploy 0640`, add `admin` to `gymdeploy` (tracker `deploy` user must not be); (4) fill `.env` per `contracts/server-env.md`, reading `Jwt__Secret`, `RESEND_API_KEY`, `TURNSTILE_SECRET_KEY` once from Azure with `az containerapp secret list --show-values` (R8, FR-010), editing only with `sudo -e`; (5) `/var/lib/gymnotebook/` owned by `gymdeploy`, a first `image-tag.env`; (6) copy `deploy/gymnotebook.caddy` to `/opt/edge/sites/` (`root:root 0644`) and run the tracker's `caddy reload` (which validates first and keeps the old config on error, R4); (7) point `api.gymnotebook.fit` (DNS-only A record) at the server; (8) `docker compose -p gymnotebook -f /opt/gymnotebook/compose.prod.yml up -d --wait`.
- [x] T021 [US2] Add § Removing Gym Notebook to `deploy/README.md`: `docker compose -p gymnotebook down`, remove `/opt/edge/sites/gymnotebook.caddy` + `caddy reload`, `DROP DATABASE gymnotebook; DROP ROLE gymnotebook;`, without stopping the tracker (spec edge case).

### Owner checks

- [x] T022 [US2] (owner) Provision the server by following T020 exactly; fix the runbook where reality differs. Pass: `curl https://api.gymnotebook.fit/health` → 200 on an empty `gymnotebook` database (migrations ran). **Passed 2026-10-12** with tag `main-7fddd33…`. Runbook fixed for: the tracker-role precondition check, re-login after joining `gymdeploy`, `/tmp/gn` lost between steps, `-f` for the tracker's stack, and a certificate attempt stuck on ZeroSSL (`caddy reload --force`).
- [x] T023 [US2] (owner) Isolation checks (quickstart § Provision): as the tracker role, `psql -d gymnotebook` → `permission denied for database`; as `gymnotebook`, `psql -d defaultdb` → the same (FR-004). `stat` shows `/etc/gymnotebook/.env` `root:gymdeploy 0640` (FR-007). `docker ps` shows no published port for `gym-api`/`gym-web` (FR-014). **Passed 2026-10-12** after T014: `tracker` → `gymnotebook` denied, `gymnotebook` → `defaultdb` denied, `tracker` → `defaultdb` connects; `.env` `root:gymdeploy 640`; no published ports (`80/tcp`, `8080/tcp`). Before T014 the `gymnotebook` → `defaultdb` check connected, because `defaultdb` was still open to `PUBLIC`.
- [x] T024 [US2] (owner) Rehearsal steps 5 and 6: `docker stop gymnotebook-gym-api-1` → our API 502, `subscriptionstrack.com` green on UptimeRobot; then start it again. A login request with a forged `X-Forwarded-For` lands in the same rate-limit bucket as one without it (FR-014). Record results in this file. **Passed 2026-10-12**: with `gym-api` stopped our API went 200 → 502 → 200 while `subscriptionstrack.com` and its `/health` stayed 200 under 1 s (probed every 2 s); 12 failed logins, each with a different forged `X-Forwarded-For`, gave 401 for 1–10 and 429 for 11–12, so the forged header did not change the bucket.
- [x] T025 [US2] (owner) Connection check (US2 scenario 2, FR-003): with `gym-api` pool at its cap, `SELECT usename, count(*) FROM pg_stat_activity GROUP BY 1` shows `gymnotebook` ≤ 10 and the tracker still connects. Record the numbers in `specs/005-upcloud-migration/research.md` R2. **Passed 2026-10-12**: under 40 parallel `/health` callers `gymnotebook` peaked at exactly 10 connections (the cap held); the tracker's `/health` stayed 200 in about 0.1 s.

**Checkpoint**: both apps on one server; the tracker unaffected by our stop/start; databases isolated.

---

## Phase 4: User Story 3 — Merges keep deploying themselves (Priority: P2)

**Goal**: a merge to `main` reaches `gymnotebook.fit` through a forced-command SSH key that can only roll Gym Notebook to a CI-built tag, with automatic rollback.

**Independent Test**: merge a trivial change and see it live within 15 minutes; deploy a known-bad tag via `workflow_dispatch` and see exit 1, the previous tag still serving, and the tracker's containers not recreated.

### Deploy script and guard test (PR 2)

- [x] T026 [P] [US3] Create `deploy/bin/deploy.sh` per `contracts/deploy-interface.md`, ported from `docker-subscription-tracker/deploy/bin/deploy.sh` with a header comment: tag read only from `$SSH_ORIGINAL_COMMAND`; reject anything not matching `^main-[0-9a-f]{40}$` → exit **64**, input not echoed; `flock /var/lib/gymnotebook/deploy.lock`; save the running tag to `/var/lib/gymnotebook/previous-tag`; write `IMAGE_TAG=<tag>` to `/var/lib/gymnotebook/image-tag.env` via temp file + rename; `docker compose -p gymnotebook -f /opt/gymnotebook/compose.prod.yml pull` → on failure restore the tag file, exit **75**; `up -d --wait --wait-timeout 120`, then `curl --resolve api.gymnotebook.fit:443:127.0.0.1 https://api.gymnotebook.fit/health` with retries → exit **0**; otherwise roll back with `up -d --wait` → exit **1**, or **2** if the rollback fails or there is no previous tag. Never `down`, `prune`, `caddy reload`, another project name, or printing the env file.
- [x] T027 [P] [US3] Create `deploy/tests/deploy-guard.sh`: runs `deploy/bin/deploy.sh` with `SSH_ORIGINAL_COMMAND` set to a valid tag (stubbed `docker`/`flock`/`curl` on `PATH` and a temp state directory, so it reaches the pull step) and to invalid inputs (empty, short sha `main-5177bdc`, uppercase hex, `main-<40 hex>; id`, `--help`, a newline) and asserts each invalid one exits 64 without echoing input or calling `docker`. Exits non-zero on any failed case.
- [x] T028 [US3] Add a step running `bash deploy/tests/deploy-guard.sh` (and `bash -n` / `shellcheck` if already available on the runner) to the `deploy-config` job in `.github/workflows/test.yml`.
- [x] T029 [US3] Make the paths `deploy.sh` needs overridable only by the guard test (e.g. a `GYMNOTEBOOK_STATE_DIR` env var defaulting to `/var/lib/gymnotebook`), documented in the script header; keep production behaviour identical to the contract.
- [x] T030 [US3] Write § Deploy in `deploy/README.md`: create Linux user `gymdeploy` (groups `gymdeploy`, `docker`; key-only login), `/home/gymdeploy/.ssh/authorized_keys` line `restrict,command="/opt/gymnotebook/bin/deploy.sh" ssh-ed25519 <key> gha-gymnotebook-deploy`, the honest note that `docker` group is root-equivalent so the forced command is the boundary (R6), the exit codes, and how to roll back by hand via `workflow_dispatch` with an older tag.

### Deploy workflow (PR 3)

- [x] T031 [US3] Add job `deploy-upcloud` to `.github/workflows/deploy.yml`: same triggers and `if:` as the Azure `deploy` job, plus `vars.UPCLOUD_DEPLOY_ENABLED == 'true'`; `environment: production`; same concurrency group; tag `main-<head_sha>` (full 40 hex) or the `workflow_dispatch` input; writes `UPCLOUD_DEPLOY_SSH_KEY` and `UPCLOUD_KNOWN_HOSTS` to temp files (`0600`), runs `ssh -i "$key" -o StrictHostKeyChecking=yes -o UserKnownHostsFile="$known" "gymdeploy@$UPCLOUD_DEPLOY_HOST" "$IMAGE_TAG"`, and fails the job on non-zero exit. Change the `workflow_dispatch` `image_tag` default from the short `main-5177bdc` to no default (required), with a description saying it must be `main-<40 hex>`. The Azure job stays unchanged until PR 5.
- [x] T032 [US3] Document the GitHub secrets `UPCLOUD_DEPLOY_SSH_KEY`, `UPCLOUD_DEPLOY_HOST`, `UPCLOUD_KNOWN_HOSTS` and variable `UPCLOUD_DEPLOY_ENABLED` in `deploy/README.md` § Deploy and `README.md` § deploy.

### Owner setup and checks

- [x] T033 [US3] (owner) Create `gymdeploy` and its key per T030; add the three secrets to the `production` environment; set `UPCLOUD_DEPLOY_ENABLED=true`. Check `ssh gymdeploy@<server> 'id; ls'` → `refused`, exit 64, and no shell (FR-006). **Passed 2026-10-12**: host-key fingerprint from `ssh-keyscan` matched the server's `ssh_host_ed25519_key.pub`; `ssh … 'id; ls'` → `deploy: refused; expected an image tag like main-<40 hex sha>`, exit 64, no shell; secrets and `UPCLOUD_DEPLOY_ENABLED=true` set on 2026-10-12, after PR #150's merge run (where `deploy-upcloud` was skipped); sending the running tag `main-7fddd33…` by hand with the key pulled both images and exited 0 (`is live`), so the key, forced command, GHCR pull and health check work end to end. CI has not yet reached the server; T034/T035 are the first runs that do.
- [x] T034 [US3] (owner) Rehearsal step 4: `workflow_dispatch` with `target: upcloud` (so Azure, still production, is untouched) and a known-bad tag (an image whose `/health` fails) → job fails with exit 1, the previous tag still serves, `docker ps` uptime of the tracker's containers unchanged (FR-005, FR-017). **Passed 2026-10-12** (run 07:16:31Z, `target: upcloud`): the bad image was the `e4b8b6c` backend with `ENTRYPOINT sleep infinity` (container up, API never listening), pushed by hand as `main-deadde…` (40 hex) with the good frontend under the same tag. Azure `deploy` skipped; `deploy-upcloud` logged `main-89b2c28… -> main-deadde…`, `gym-api is unhealthy` at 07:18:24 (after the 120 s wait), `rolling back`, `rolled back to main-89b2c28…` at 07:18:48, exit 1. Owner confirmed `image-tag.env` back on `main-89b2c28…`, `/health` 200 afterwards, and the tracker's container uptimes unchanged. Cleanup: the bad backend version deleted from GHCR; the frontend tag `main-deadde…` remains because GHCR shares that version with the real `main-e4b8b6c…` frontend, and it points at that good image.
- [x] T035 [US3] (owner) Merge a trivial change and time it from merge to the new tag serving at `https://api.gymnotebook.fit/health` and `gymnotebook.fit` (rehearsal hostnames): ≤ 15 minutes with no manual step (SC-004). Record it in this file. **Passed 2026-10-12** with PR #151 (`e4b8b6c`, docs-only): merged 06:55:50Z, `build-and-push` done ~06:56:45Z, `deploy-upcloud` logged `main-7fddd33… -> main-e4b8b6c…` and `is live` at 06:57:07Z: **1 min 17 s**, no manual step; first CI deploy over SSH (`StrictHostKeyChecking=yes`) worked. `api.gymnotebook.fit/health` → 200. `gymnotebook.fit` cannot be checked through UpCloud before cutover (DNS still points at Azure, so Caddy has no certificate for it; TLS handshake fails); `up --wait` did wait for `gym-web` healthy. The build took 47 s; the last eight builds all took under a minute, though none changed app code, so a cold rebuild is untested (the margin to 15 minutes is large).

**Checkpoint**: both Azure and UpCloud deploy on merge; a bad version never stays live on UpCloud.

---

## Phase 5: User Story 1 — The notebook is all there after the move (Priority: P1) 🎯 MVP

**Goal**: all Neon data lands unchanged in `gymnotebook` on UpCloud, `gymnotebook.fit` serves from the server with valid TLS, and sign-in, email, Turnstile, caps, rate limits and backup/restore behave as before.

**Independent Test**: identical `verify-copy.sql` output on Neon at the freeze and on UpCloud after the restore; sign in with the existing password; notebook, chart and last-backup date unchanged; signup → confirm → sign in → reset with a second address; Turnstile widget appears; no certificate warning on either hostname.

### Copy check and runbooks (PR 2)

- [x] T036 [P] [US1] Create `deploy/bin/verify-copy.sql` modelled on `docker-subscription-tracker/deploy/bin/verify-copy.sql`: one row per application table in schema `public` (excluding `__EFMigrationsHistory`), columns `table_name, row_count`, ordered by name, using exact `count(*)` (not statistics), then the last `__EFMigrationsHistory.migration_id`. Output must be byte-identical when run on two databases with the same data (data-model § Copy check). Header comment says how to run it with `psql -XAt -f`.
- [x] T037 [P] [US1] Add a CI step to the `deploy-config` job in `.github/workflows/test.yml` that runs `deploy/bin/verify-copy.sql` against a `postgres:18` service after applying the app's migrations (or against an empty schema with `__EFMigrationsHistory`), so a SQL error fails CI.
- [ ] T038 [US1] Write § Cutover in `deploy/README.md` per quickstart § Cutover and FR-011: lower the apex TTL a day before; owner stops logging; on Neon `ALTER DATABASE … SET default_transaction_read_only = on`; from the server in `postgres:18`: `pg_dump -Fc --no-owner --no-privileges` over Neon's **unpooled** endpoint with `sslmode=require`, `pg_restore --no-owner --no-privileges --exit-on-error` as `gymnotebook` into the **empty** database with `gym-api` stopped, `ANALYZE`; `verify-copy.sql` on both sides and `diff`; start `gym-api`; switch the `gymnotebook.fit` A record; smoke test (T049's list).
- [ ] T039 [US1] Write § Rollback in `deploy/README.md`: A record back to `20.240.228.206`, `ALTER DATABASE … SET default_transaction_read_only = off` on Neon, anything written on UpCloud re-entered by hand; valid only while Azure and Neon exist (data-model § State transitions).
- [ ] T040 [US1] Write § Restore in `deploy/README.md` (FR-020, research R2): restore the service's backup into a **new** temporary service, `pg_dump -Fc -d gymnotebook` from it, stop `gym-api`, `pg_restore --clean --if-exists --no-owner --no-privileges` into the live `gymnotebook` as `gymnotebook`, `ANALYZE`, start `gym-api`, delete the temporary service; note it never touches the tracker's database, bills hourly only while it exists, and the in-app JSON backup (spec 004) remains the user's long-term copy.
- [ ] T041 [US1] Write § Emptying the database after rehearsal in `deploy/README.md`: stop `gym-api`, `DROP DATABASE gymnotebook` + recreate with the T020 statements (owner, `REVOKE`/`GRANT`), start `gym-api` so migrations re-run (quickstart rehearsal step 7).
- [x] T042 [US1] Verify PR 2 as a whole: `deploy-config` passes in CI; `docker compose config` locally with `deploy/tests/sample.env`; every new file starts with a purpose comment (Principle III). Open PR 2 after owner approval. **Passed 2026-10-11**: `deploy-config` green on PR #147; locally the stack ran with real Caddy on `edge` (healthchecks, 200/308/502 behavior, no access log).

### Rehearsal and G2 (owner, after PR 3)

- [x] T043 [US1] (owner) Rehearsal step 1: dry-run copy Neon → `gymnotebook` without freezing per T038; time it; `verify-copy.sql` outputs match apart from rows written since. Record the time in `specs/005-upcloud-migration/research.md` R9.**Skipped by owner decision 2026-10-12**: the only user is the owner, so the copy time (the freeze length) does not matter; the cutover's own `verify-copy.sql` diff runs before DNS moves, and a failed copy is retried with Neon unfrozen and Azure still serving. Accepted cost: runbook mistakes in § Cutover surface at T048 instead of here. T044 still runs; it protects the tracker, not our data.
- [x] T044 [US1] (owner) Rehearsal steps 2–3 (G2 capacity): restore the 100,000-set reference backup on a test account at `api.gymnotebook.fit`, then export it. `docker inspect -f '{{.State.OOMKilled}}' gymnotebook-gym-api-1` → `false`, `free -m` stays under 80%, tracker `/health` under 1 s throughout; then with both stacks idle `available` ≥ 30% (SC-006). Record in `specs/005-upcloud-migration/research.md` R1. **Passed 2026-10-12**: no reference file exists, so the tests' `SeedReferenceNotebookAsync` SQL seeded 1,000 pages × 10 blocks × 10 sets into test account A, inserted with test account B straight into `users` (confirmed, `@example.invalid`, so no Turnstile or email). Over `api.gymnotebook.fit` (08:10:18–08:10:49 UTC): export A 200 in 2.1 s (10.2 MB); restore into B 200 in 27.3 s (`pagesAdded` 1000, `setsAdded` 100000); export B 200 in 1.3 s (10.4 MB). Memory peaked at 57% used (limit 80%); tracker `/health` slowest 0.12 s, all 200 (limit 1 s); `OOMKilled` `false`; idle afterwards ≥ 48% `available` (SC-006, limit 30%). Lesson: the first attempt's logins returned 400, most likely because the password broke the hand-built JSON body; a letters-and-digits test password fixed it.
- [x] T045 [US1] (owner) Rehearsal step 7: delete the test account and empty the database per T041. **G2 passes** only when T023–T025, T033–T035 and T044 all passed (T043 skipped, see there); otherwise stop (FR-002). **Passed 2026-10-12**: `gym-api` stopped; as `upadmin` `gymnotebook` dropped and recreated with the T020 owner/`REVOKE`/`GRANT` statements; the `gymnotebook` role's password rotated at the same time (it had appeared in a chat transcript) and `/etc/gymnotebook/.env` updated; `gn up -d --wait` re-ran the migrations and `/health` → 200 with the new password. Both test accounts' logins now return 401; the test files are deleted. **G2 passed** (T023–T025, T033–T035, T044; T043 skipped). The database is empty and ready for cutover.

### Privacy docs (PR 4, before cutover)

- [x] T046 [P] [US1] Update `docs/privacy/suppliers.md` (UpCloud added as processor in Helsinki, Finland; Azure Container Apps/Log Analytics and Neon marked as ending at cutover; reuse the tracker's UpCloud DPA review if one exists), `docs/privacy/retention.md` (server logs: Docker `json-file` 3 × 10 MB per container, size-bounded; Caddy writes no access log for our hosts, errors only; provider backups 3 days), `docs/privacy/notice-candidate.json` (location/processor text), and draft the P1/P2 "Where it lives" amendment in `docs/privacy/processing-decision.md` for the owner (R11, FR-023). **Drafted 2026-10-12**: no tracker UpCloud DPA review existed, so UpCloud's Terms of Service and DPA (effective 2026-07-03) were read in the browser and reviewed in `suppliers.md` (processor; no subprocessors for EU data centres). Three open points go to the owner with the T047 signature: Docker's size-bounded logs can outlive the 30-day maximum; the cutover dump and T050 archive need destroying by cutover +30 days; and ToS §1.2's business-use clause against a personal account.
- [x] T047 [US1] (owner) Review and sign the `docs/privacy/processing-decision.md` amendment; merge PR 4. Cutover must not start before this. **Signed 2026-10-12** (PR #156 merged): (1) a daily job trims Gym Notebook's container logs to 30 days, added as T061 and required before cutover; (2) `/tmp/neon.dump` and the T050 archive are destroyed by cutover + 30 days; (3) ToS §1.2 accepted on the personal account.
- [x] T061 [US1] Add a daily job that removes lines older than 30 days from `gym-api`'s and `gym-web`'s Docker logs on the server (retention.md, signed T047 decision 1); runbook step in `deploy/README.md`; owner installs it before T048. **Implemented 2026-10-12**: `deploy/bin/trim-logs.sh` plus `deploy/systemd/gymnotebook-log-trim.{service,timer}`, tested by `deploy/tests/trim-logs-test.sh` in the `deploy-config` CI job; § Log time limit in `deploy/README.md`. **Installed 2026-10-12**: all § Log time limit checks passed (timer scheduled, manual run `status=0/SUCCESS`, script `root:root 755`, no log line older than 30 days). Lesson: `journalctl` needs `sudo` for `admin`; the runbook now says so.

### Cutover (owner)

- [x] T048 [US1] (owner) Run § Cutover (T038): freeze, copy, `ANALYZE`, `verify-copy.sql` identical on both sides (SC-002, FR-008); switch the apex A record; Caddy issues the certificate. This is the first real copy (T043 was skipped): expect to fix § Cutover where it differs, and if the copy or the `diff` fails, turn Neon's read-only off and retry another day. **Done 2026-10-12**, without lowering the TTL first: the record was already on Cloudflare's Auto TTL (300 s), so stale lookups could reach Azure for at most 5 minutes, and Azure could only read because Neon was frozen. Dump taken 09:24 UTC; `pg_restore -l` before the restore showed only our tables, sequences, keys and indexes (no extension or schema comment). `verify-copy.sql` identical on both sides: users 1, exercises 8, workouts 8, workout_exercises 47, set_entries 134, email_sends 2, latest migration `20261008113200_AddWorkoutRevision`. Lessons: the first dump used Neon's pooled host by mistake (harmless while frozen; the verify ran unpooled); the Neon role's password appeared in a chat transcript, so the Neon project was deleted after the smoke test instead of rotating it (T056).
- [x] T049 [US1] (owner) Smoke test: no certificate warning on `gymnotebook.fit` and `api.gymnotebook.fit`; sign in with the existing password (or an existing session survives, FR-010); notebook, chart and last-backup date unchanged; log one set; take a JSON backup; signup → confirmation email → sign in → password reset with a second address; Turnstile widget appears. On failure run § Rollback (T039). Record the outcome in this file. **Passed 2026-10-12**: every item above, including signup, confirmation, sign-in and password reset with a second address, and the Turnstile widget.

**Checkpoint**: UpCloud is production; Azure and Neon still exist as the rollback.

---

## Phase 6: User Story 4 — Azure and Neon are gone (Priority: P3)

**Goal**: no Azure or Neon resource or credential tied to this app exists, and the docs describe UpCloud as production.

**Independent Test**: the Azure resource group and Neon project are gone; `rg -i "azure|neon|containerapp|azurecontainerapps"` finds only historical notes (specs, milestone history, signed privacy history).

### Before PR 5 (owner, same day as cutover)

- [x] T050 [US4] (owner) Archive a final Neon `pg_dump -Fc`; check it by restoring into a throwaway `postgres:18` and comparing `verify-copy.sql` output to the cutover output; keep the archive offline, and destroy it and `/tmp/neon.dump` by cutover + 30 calendar days, recording the date (signed T047 decision 2). **Skipped by owner decision 2026-10-12**: no archive was made. The smoke test passed, the owner's JSON backup from it and UpCloud's 3-day backups are the copies outside the live database, and an archive would have had to be destroyed within 30 days anyway (T047 decision 2). `/tmp/neon.dump` and the copy-check files were removed from the server the same day.

### PR 5 — UpCloud is production

- [x] T051 [US4] Delete `infra/` (`main.bicep`, `main.parameters.json`, `bicepconfig.json`, `modules/`) and the Azure `deploy` job from `.github/workflows/deploy.yml`; drop `id-token: write`, the `UPCLOUD_DEPLOY_ENABLED` gate and the `workflow_dispatch` `target` input (UpCloud is now the only target), keeping the `workflow_dispatch` rollback. **Done 2026-10-12**: `deploy-upcloud` is the only job, run after a successful `build-and-push` or by hand with an `image_tag`; `deploy/README.md` § Deploy no longer mentions the gate or `target`.
- [x] T052 [P] [US4] Update comments that name Azure, Container Apps or Neon to describe Caddy on UpCloud: `backend/GymNotebook.Api/Program.cs` (lines 39, 65, 487, 546 and the `/health` `.WithDescription` at 555), `backend/GymNotebook.Api/GymNotebook.Api.csproj` (line 29), `backend/GymNotebook.Api/EmailOptions.cs` (line 47), `backend/GymNotebook.Api/AccountLifecycle.cs` (lines 24, 161: the `SET LOCAL` reasoning stays correct without Neon's pooler; say so), `frontend/nginx.conf` (line 9: logs now live in Docker's size-bounded `json-file`). Comments and the OpenAPI description only; no behaviour change. **Done 2026-10-12**, comments and the `/health` description only. Also reworded, because T055's search would flag them: `docker-compose.yml`, `ForwardedHeadersGymNotebookFactory.cs`, `TransitionClearingSqlTests.cs` and `deploy/bin/verify-copy.sql`, plus `docs/privacy/retention.md`'s transition-clearing "who and where" (psql from the server) and the backup line under it, which the test comments mirror. Checks: `dotnet format --verify-no-changes`, `dotnet test` 463/463.
- [x] T053 [P] [US4] Update `README.md`: stack table (UpCloud server + managed PostgreSQL 18 in Helsinki, Caddy), deploy section (link to `deploy/README.md`, secrets now on the server, the forced-command deploy), configuration (production values in `/etc/gymnotebook/.env`), and remove Azure/Neon/Bicep instructions. **Done 2026-10-12**: stack, CI/CD and hosting rows, configuration (production env file), `FORWARDED_HEADERS_ENABLED` (Caddy), repository layout (`infra/` gone, workflows line), status. Also removed the two Neon agent skills from `skills-lock.json`, which README tells readers to reinstall from; they were not installed.
- [x] T054 [P] [US4] Update `PLAN.md`: § Stack, § Configuration and § Secrets (production row → server env file), § Rate limiting (Caddy replaces `X-Forwarded-For`; `FORWARDED_HEADERS_ENABLED` set in `deploy/compose.prod.yml`), § Testing and CI (`deploy-config` job), health-check text, and § Milestones: milestone 15 entry with what the move turned out to involve. **Done 2026-10-12**: also the Goals line, the API-docs "deployed app" note and `deploy.yml` in § Testing and CI.
- [x] T055 [US4] Verify PR 5: `rg -i "azure|neon|containerapp|bicep"` outside `specs/`, milestone history and signed privacy records returns nothing; `dotnet format --verify-no-changes`, `dotnet test`, and the frontend checks pass; the `deploy-config` job passes; a merge of PR 5 deploys via `deploy-upcloud` alone. Merge before T056 so no merge runs the Azure job against deleted resources. **Checked 2026-10-12, before merge**: the search leaves only deliberate history (PLAN milestones and "moved from Azure" notes, README status, `deploy/README.md` § Cutover's record) and a false positive in `docs/ui/prototype.html`'s compressed data. To pass it, `deploy/README.md` also changed: the three "from Azure" secrets now come from the current env file, Provision's DNS step no longer assumes Azure, and § Cutover/§ Rollback became a short record pointing at commit `9cbfdd8`; § Restore inlines the count check and links the deletion replay in `docs/privacy/restore.md`. `dotnet format --verify-no-changes`, `dotnet test` 463/463, frontend typecheck/lint/format/Vitest 353/353, deploy guard and log-trim tests, `compose config`: pass. **Passed after merge (#160)**: `test` (with `deploy-config`) and `build-and-push` green, and the `deploy` run had one job, `deploy-upcloud`, which succeeded; `api.gymnotebook.fit/health` 200.

### Decommission (owner, after PR 5 merges)

- [x] T056 [US4] (owner) Delete the Azure resource group `rg-gymnotebook-prod`, the Entra OIDC app registration, the Neon project, and the GitHub secrets `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `NEON_CONNECTION_STRING`, `JWT_SECRET`, `TURNSTILE_SECRET_KEY`, `RESEND_API_KEY` (keep the `TURNSTILE_SITE_KEY` variable only if something still reads it) (FR-021, SC-007). **Neon part done 2026-10-12**: project `dawn-pine-04463679` deleted by the owner after the passed smoke test, ending the Neon rollback; this also retired the `gymnotebook_owner` password that appeared in a chat transcript. **Azure part done 2026-10-12**, after PR 5 (#160): resource group `rg-gymnotebook-prod` deleted (about 19 minutes); Entra app `gymnotebook-production-deploy` deleted (no service principal left); the GitHub secrets removed at both repository and `production` environment level (`AZURE_*`, `JWT_SECRET`, `NEON_CONNECTION_STRING`, `RESEND_API_KEY`, `TURNSTILE_SECRET_KEY`), as were the variables `UPCLOUD_DEPLOY_ENABLED` and `TURNSTILE_SITE_KEY`; only the three `UPCLOUD_*` secrets remain. Lesson: deleting the resource group only **soft-deletes** a Log Analytics workspace, recoverable with its data for 14 days, which would have kept pre-cutover log lines past the 30-day maximum (up to about 43 days). The owner purged it: recreated the group, recovered `log-gymnote-prod-58dd`, deleted it with `--force`, deleted the group again; `list-deleted-workspaces` no longer shows it.
- [x] T057 [US4] (owner) Add UptimeRobot keyword monitors for `https://gymnotebook.fit` and `https://api.gymnotebook.fit/health`, alerting after 5 minutes down (FR-018, D9). **Done 2026-10-12**, in the owner's existing UptimeRobot account beside the tracker's monitors: `https://gymnotebook.fit` with keyword `id="root"` (language-independent, ahead of a Finnish UI; a comment in `frontend/index.html` points back here) and `https://api.gymnotebook.fit/health` with keyword `reachable`, both alerting after 5 minutes. The tracker's `ops-check` already covers server memory, disk, database size and, via `EXTRA_CERT_HOSTS` (confirmed set), both hostnames' certificates.

**Checkpoint**: one hosting provider; docs match production.

---

## Phase 7: Polish & Cross-Cutting Concerns

- [x] T062 Rewrite `docs/privacy/restore.md` for UpCloud (replaces the Neon branch and point-in-time steps): restore into a temporary service per `deploy/README.md` § Restore, keep `gym-api` stopped, replay `deletion.committed` lines from `gym-api`'s Docker log dated after the backup, verify, then start. Remove the outdated banner. Owner reviews; T058 waits for this. **Done 2026-10-12**: the live database is now the preserved state (UpCloud restores into a temporary service, so reconciliation happens there before the copy back); the fallback reads `gym-api`'s Docker log with a per-minute health-line gap check. The primary path's pipe and SQL, and the gap check, were run against throwaway Postgres 18 containers. Finding: every deploy recreates `gym-api` and Docker deletes the old container's log, so the fallback only covers deletions since the last deploy; the runbook checks the container's creation time against `T`. On the server the healthcheck's `SELECT 1` lines were confirmed (6 a minute). Owner reviews the runbook in this PR.
- [ ] T063 Keep the deletion evidence across deploys: Docker deletes `gym-api`'s log when a deploy recreates the container, so restore.md's fallback (step 4) only covers deletions since the last deploy. Option: `deploy.sh` appends the old container's `deletion.*` lines to a root-only file under `/var/lib/gymnotebook/` before `up`, trimmed to 30 days by the log trim job, and restore.md reads that file too. Needs a privacy review of the new copy (retention.md row).
- [ ] T058 (owner) (after T062) Restore drill (FR-020, quickstart § Restore drill): restore the latest service backup into a new service, `pg_dump -d gymnotebook`, restore into a scratch database on the live service, compare `verify-copy.sql` to the counts as of that backup, delete the temporary service and scratch database. Pass: under 1 hour. Record in this file and fix `deploy/README.md` § Restore where it differed.
- [ ] T059 (owner) SC-005: after 2 hours with no traffic, first page load and first sign-in are as fast as warm ones. Record in this file.
- [ ] T060 (owner) After 30 days: UpCloud invoice unchanged at €15.40 ± €0.30 excl. VAT, no Azure/Neon charges for this app (SC-001); tracker availability ≥ 99.5% with no Gym Notebook-caused incident (SC-003). Record in this file and mark the spec done in `PLAN.md` § Milestones.

---

## Dependencies & Execution Order

### Phase dependencies

- **Setup (Phase 1)**: T001 done; T002 before T020 is written up as final.
- **Foundational (Phase 2)**: PR 1 (T003–T008) merges first. PR 2's shared files (T009–T013) go in one PR together with T020–T021, T026–T030 and T036–T042.
- **US2 (Phase 3)**: tracker PR (T014–T019) must be applied on the server before provisioning (T022). T014 is applied before anything else in that PR.
- **US3 (Phase 4)**: needs PR 2 merged and the server provisioned (T022). PR 3 (T031–T032) after PR 2.
- **US1 (Phase 5)**: G2 (T045) needs T023–T025 and T033–T035. Cutover (T048) needs G2, PR 4 signed and merged (T047), and the log trim job installed (T061).
- **US4 (Phase 6)**: needs a passed smoke test (T049). T050 (archive) → PR 5 merged (T051–T055) → T056 (delete Azure, Neon and secrets). T057 any time after cutover.
- **Polish (Phase 7)**: after cutover; T060 at +30 days.

Priority order is US1 = US2 (P1) > US3 (P2) > US4 (P3), but US1's cutover is last among the three because it depends on the isolation and deploy checks.

### PR map

| PR | Repo | Tasks |
|---|---|---|
| 1 Postgres 18 | gym | T003–T008 |
| 2 `deploy/` | gym | T009–T013, T020–T021, T026–T030, T036–T042 |
| T Edge and ops | tracker | T014–T018 |
| 3 Deploy workflow | gym | T031–T032 |
| 4 Privacy docs | gym | T046–T047 |
| 5 UpCloud is production | gym | T051–T055 |
| owner-only | — | T001–T002, T019, T022–T025, T033–T035, T043–T045, T048–T050, T056–T057, T058–T060 |

### Parallel opportunities

- PR 1: T003, T004, T005, T006, T007 touch different files.
- PR 2: T010, T011 alongside T009; T026, T027 together; T036, T037 together; runbook sections (T020, T030, T038–T041) are one file, so sequential.
- Tracker PR: T014, T015, T016, T017 touch different files.
- PR 1 and the tracker PR can be written in parallel (different repositories, no shared files).
- PR 4 (T046) can be drafted any time after PR 2; only the signature (T047) gates cutover.
- PR 5: T052, T053, T054 together.

### Parallel example: PR 1

```text
T003 GymNotebookFactory.cs → postgres:18
T004 TwoHostGymNotebookFixture.cs → postgres:18
T005 docker-compose.yml → postgres:18, db_data_18
T006 constitution.md → Principles I, IV, V
T007 README.md / PLAN.md / AGENTS.md version text
```

### Parallel example: tracker PR

```text
T014 tracker-only role (runbook + env)
T015 Caddyfile import
T016 compose.prod.yml mount + edge network
T017 ops-check.py total size + hostnames
```

---

## Implementation Strategy

### MVP

The MVP is a working cutover (US1), which here cannot be separated from US2's isolation: PR 1 → PR 2 → tracker PR → provision → rehearsal (US2 checks, G2) → PR 4 → cutover. US3 (PR 3) is in the plan before the rehearsal because rehearsal step 4 tests its rollback; if it slips, the owner can deploy by running `deploy.sh` through the `gymdeploy` key by hand, and cutover can still happen with Azure's deploy left running only until PR 5.

### Incremental delivery

1. PR 1 → suite and Compose on Postgres 18; nothing in production changes.
2. PR 2 + tracker PR → server provisioned beside the tracker on an empty database; tracker unchanged for its users.
3. PR 3 → merges deploy to both Azure and UpCloud.
4. Rehearsal + G2 → capacity and isolation measured under the 100,000-set load.
5. PR 4 + signature → cutover → smoke test.
6. Final Neon archive → PR 5 → decommission: Azure and Neon gone; docs describe UpCloud.
7. Restore drill and 30-day checks.

### Stop points

- G1 (T001) or role creation (T002) fails → stop, give the cheapest option and its price (FR-002).
- G2 (T045) fails → stop before cutover; Azure and Neon stay production.
- Smoke test (T049) fails → § Rollback; Azure and Neon are not deleted.
