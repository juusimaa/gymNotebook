# Implementation Plan: Move Hosting from Azure to UpCloud

**Branch**: `005-upcloud-migration` | **Date**: 2026-10-11 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/005-upcloud-migration/spec.md`

**Status**: Draft for owner review (Principle VII). Decisions D1–D10 are proposals until this plan is merged; D2, D3 and D5 were confirmed by the owner on 2026-10-11 (§ Resolved questions). **Gate G1 ran on 2026-10-11 and passed for server capacity, connections and storage** ([research R1](research.md#r1-capacity-on-the-shared-server), [R2](research.md#r2-sharing-the-managed-postgresql-service)). It found the tracker connecting as `upadmin`, so the tracker-only role (§ Tracker repository changes, item 5) is required.

## Summary

Gym Notebook moves onto Subscription Tracker's existing UpCloud resources and
adds nothing to the bill:

- **Server**: two more containers, `gym-api` and `gym-web`, in their own Compose
  project `gymnotebook` under `/opt/gymnotebook`. They have hard memory and CPU
  caps, so a problem in Gym Notebook can't take the tracker down.
- **Edge**: the tracker's Caddy stays the only thing on ports 80/443. It imports
  a site file this repository owns (`deploy/gymnotebook.caddy`) and reaches our
  containers over a shared Docker network.
- **Database**: a separate `gymnotebook` database and role in the same managed
  PostgreSQL 18 service, with a pool capped at 10 connections. Tests and Compose
  move to Postgres 18 to match.
- **Deploys**: same model as the tracker. A separate `gymdeploy` user's SSH key
  can only run `deploy.sh <tag>`, which health-checks the new version and rolls
  back by itself.
- **Cutover**: short. Freeze Neon read-only, copy with `pg_dump`/`pg_restore`,
  compare row counts, switch two DNS records, smoke-test. Azure and Neon are
  deleted the same day.

The tracker repository needs one small PR (§ Tracker repository changes).

See [research.md](research.md), [data-model.md](data-model.md), the contracts
([server env](contracts/server-env.md), [edge](contracts/edge-http.md),
[deploy](contracts/deploy-interface.md)) and [quickstart.md](quickstart.md).

## Technical Context

**Language/Version**: unchanged app code: C# / .NET 10, TypeScript, React 19.
New: bash (`deploy.sh`), Caddyfile syntax, Compose YAML, SQL (copy check).

**Primary Dependencies**: no new app dependency. On the server: Docker Compose,
`caddy:2.11.7` (the tracker's), `postgres:18` for dump, restore and psql.

**Storage**: UpCloud Managed PostgreSQL 18, `rdb.development.1CPU-1GB`, 10 GiB,
`fi-hel1`, shared with the tracker. **No schema change and no migration.**

**Testing**: the existing xUnit + Testcontainers suite on `postgres:18`. The
deploy pieces get a CI job for `docker compose config` with a sample env,
`caddy validate` on the site file, and a shell test of `deploy.sh`'s tag guard.
The copy is checked with `verify-copy.sql`. The rest is owner-run quickstart
scenarios.

**Target Platform**: Ubuntu 24.04 UpCloud Starter `1xCPU-2GB` (shared), Docker.

**Project Type**: web service (API + static frontend). This is a hosting change.

**Performance Goals**: SC-005, warm-equivalent first request after 2 h idle
(nothing scales to zero). SC-004, a merge is live in 15 minutes or less.

**Constraints**: no added cost (FR-001); `gym-api` limited to 512 MB and 0.75
CPU, `gym-web` to 64 MB (R1); 10 database connections (R2); the tracker is
never restarted or reconfigured by a Gym Notebook deploy (FR-005).

**Scale/Scope**: one active user; data well under 1 GB; reference load is the
100,000-set notebook from spec 001/004.

## Constitution Check

*GATE: checked before Phase 0 and again after Phase 1. Result: passes with two
amendments made in the implementing PRs, listed under Complexity Tracking.*

| Principle | Status |
| --- | --- |
| I. Architecture | **Amendment needed.** The app architecture is unchanged, and the backend and frontend images and local Compose are kept. The stated "PostgreSQL 17" becomes 18 (FR-009, R3). |
| II. Focused changes | Pass. Six PRs here plus one in the tracker repo, in the order under Delivery sequence. |
| III. Learning | Pass. Every new file starts with a comment saying what it is for, and every non-default setting gives its reason, as in the tracker's `deploy/`. |
| IV. Verification | Pass. The suite runs against Postgres 18 and the copy is checked by row counts. The deploy guard, Compose file and site file get CI checks. Edge and isolation behavior is checked by the owner's quickstart scenarios, since there's no staging server. |
| V. Security & privacy | **Amendment needed.** The text says production secrets live "in Container Apps secrets through `secretref`". They move to `/etc/gymnotebook/.env` (`root:gymdeploy 0640`, never in images or the repo), as the tracker does. All controls are kept: rate limits on the real client address (R5), CORS, Turnstile, email caps, no published API port, encrypted private database access, separate database roles (R2). The supplier, location and log changes go through the privacy docs, with an owner-signed processing-decision amendment before cutover (R11, FR-023). |
| VI. Docs aligned | Pass. README (deploy, configuration), PLAN.md (stack, configuration table, rate limiting, health, milestones), code comments that name Azure, and `docs/privacy/` all change in the PRs that change the behavior. No UI change, so `docs/ui/` is untouched. |
| VII. Reviewed plans | This document is the draft under review. |

**Post-design re-check (after Phase 1)**: unchanged. The contracts add no app
route, schema or dependency. The edge contract keeps the no-access-log decision
of `frontend/nginx.conf`.

## Design decisions

**D1 — Co-host and cap.** Separate Compose project `gymnotebook` with
`mem_limit`/`cpus` on both containers (R1). The tracker's containers are left as
they are. Our caps decide which container the kernel kills under memory
pressure.

**D2 — The tracker owns the edge; we own our site file.** One `import` line in
the tracker's Caddyfile plus a shared external network `edge` (R4). Our services
are named `gym-api`/`gym-web`, never `backend`/`frontend`, because Docker would
otherwise register a second `backend` alias on the network Caddy uses to reach
the tracker.

**D3 — API at `api.gymnotebook.fit`.** It mirrors the tracker. `config.js` and
`CORS_ORIGINS` keep their current roles. That record can point at the server
before cutover, so its certificate exists before the switch, and the rehearsal
runs at the real URL.

**D4 — Separate database and role, CONNECT revoked from PUBLIC** on both
databases (R2). `Maximum Pool Size=10` lives in the connection string, not in
code, because the limit belongs to this deployment.

**D5 — Postgres 18 everywhere** (R3), with the Compose volume moved to the new
mount path under a new name.

**D6 — Deploy like the tracker** (R6). It uses a forced-command SSH key for a
new `gymdeploy` user, a tag guard for `main-<40 hex>`, and a health check
through Caddy with automatic rollback. CI runs it after `build-and-push`,
gated by `UPCLOUD_DEPLOY_ENABLED`. App secrets leave GitHub.

**D7 — Health without new packages** (R7). bash `/dev/tcp` in Compose for
`gym-api`, busybox `wget` for `gym-web`.

**D8 — Short cutover** (R9). Freeze Neon read-only, copy, verify, switch DNS for
the apex (the API record was switched at rehearsal). Rollback is DNS back to
Azure plus Neon read-write, while Azure still exists.

**D9 — Operations reuse the tracker's.** Two UptimeRobot keyword monitors are
added (web and API `/health`). The tracker's `ops-check` gains our two hostnames
for certificate expiry, and its database-size check measures the whole service.
Memory and disk alerts are host-wide already.

**D10 — No edge access log for our hostnames** (R11). This keeps the privacy
decision behind `access_log off` in `frontend/nginx.conf`.

## Tracker repository changes

One PR in `docker-subscription-tracker`. It merges and is applied on the server
before G2:

1. `deploy/Caddyfile`: `import /etc/caddy/sites/*.caddy`, with a comment.
2. `deploy/compose.prod.yml`: mount `/opt/edge/sites:/etc/caddy/sites:ro`, and
   join `caddy` to the external network `edge` (plus `default`).
3. `deploy/bin/ops-check.py`: the database-size check uses
   `sum(pg_database_size(datname))` over the service's databases, and
   `gymnotebook.fit` and `api.gymnotebook.fit` join the certificate hosts.
   Reading another database's size needs `pg_read_all_stats` granted to the
   check's role (verify; otherwise a second read-only URL).
4. `deploy/README.md` § Provision: create the `edge` network before `up`.
5. A tracker-only role owning `defaultdb`, applied first. G1 found the tracker
   connecting as `upadmin`, and FR-004 cannot hold otherwise.

## Delivery sequence

| # | PR / step | Repo | Contents |
|---|---|---|---|
| 0 | This plan | gym | spec, plan, research, contracts, quickstart |
| G1 | **Gate**, owner | — | measurements in [quickstart § G1](quickstart.md#g1-capacity-gate-before-any-change). Stop if any fails. |
| 1 | Postgres 18 | gym | Testcontainers and Compose on `postgres:18`, the volume change, constitution amendment for I and V |
| 2 | `deploy/` | gym | `compose.prod.yml`, `gymnotebook.caddy`, `bin/deploy.sh`, `bin/verify-copy.sql`, `deploy/README.md` (provision, cutover, restore runbooks), CI `deploy-config` job |
| T | Edge and ops | tracker | § Tracker repository changes |
| 3 | Deploy workflow | gym | `deploy-upcloud` job gated by `UPCLOUD_DEPLOY_ENABLED`; the Azure deploy stays |
| — | Provision and rehearse, owner | — | quickstart § Provision, § Rehearsal, **G2** |
| 4 | Privacy docs | gym | suppliers, processing-decision amendment (owner signs), notice candidate, retention |
| — | Cutover, owner | — | quickstart § Cutover |
| 5 | UpCloud is production, Azure removed | gym | README, PLAN.md milestone 15, comments; delete `infra/`, the Azure workflow and Azure/Neon/app GitHub secrets (FR-021, FR-022) |

## Project Structure

### Documentation (this feature)

```text
specs/005-upcloud-migration/
├── spec.md
├── plan.md              # this file
├── research.md          # Phase 0
├── data-model.md        # Phase 1: server, database and file layout (no app schema change)
├── quickstart.md        # Phase 1: gates, provision, rehearsal, cutover, restore checks
├── contracts/
│   ├── server-env.md    # every production variable and where it lives
│   ├── edge-http.md     # what Caddy promises for our two hostnames
│   └── deploy-interface.md  # the deploy key, deploy.sh and its exit codes
├── checklists/requirements.md
└── tasks.md             # Phase 2, /speckit-tasks (not created here)
```

### Source Code (repository root)

```text
deploy/                          # new
├── README.md                    # environment, provision, cutover, restore, rollback
├── compose.prod.yml             # gym-api, gym-web; joins external network `edge`
├── gymnotebook.caddy            # site blocks imported by the tracker's Caddy
├── bin/
│   ├── deploy.sh                # forced command of the gymdeploy key
│   └── verify-copy.sql          # per-table row counts + last migration
└── tests/
    ├── sample.env               # fake values for `compose config` in CI
    └── deploy-guard.sh          # tag guard: valid tag passes, anything else exits 64

docker-compose.yml               # postgres:18, volume at /var/lib/postgresql
backend/GymNotebook.Tests/GymNotebookFactory.cs   # postgres:18
backend/GymNotebook.Api/Program.cs                # comments only (Azure → Caddy)
.github/workflows/deploy.yml     # PR 3 adds the UpCloud job; PR 5 removes the Azure job
.github/workflows/test.yml       # PR 2 adds deploy-config
.specify/memory/constitution.md  # PR 1: Principles I and V
infra/                           # deleted in PR 5
docs/privacy/                    # PR 4
README.md, PLAN.md               # PR 5 (PR 1 for the Postgres version)
```

**Structure Decision**: production config goes in a new top-level `deploy/`, as
in the tracker, so the two can be compared side by side. App code
(`backend/`, `frontend/`) is unchanged apart from comments.

## Resolved questions (owner, 2026-10-11)

1. **Edge ownership (D2)**: a small tracker PR, with the tracker's Caddy
   importing our site file. Accepted as recommended.
2. **API hostname (D3)**: `api.gymnotebook.fit`. Accepted as recommended.
3. **Local dev data (D5)**: the local Postgres 17 volume is left behind, not
   migrated, when Compose moves to 18. Accepted.

## Complexity Tracking

| Deviation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|-------------------------------------|
| Constitution I: PostgreSQL 17 → 18 | The shared managed service runs 18; Principle IV requires tests to match production | A second database service on 17 costs at least €9.40/month (FR-001) |
| Constitution V: secrets in a server env file, not Container Apps `secretref` | Container Apps goes away | No secret store without extra cost or service; a root-owned `0640` file is the tracker's accepted pattern |
| A change in another repository | Only one process can own ports 80/443 | A second edge on other ports would not serve standard HTTPS |
