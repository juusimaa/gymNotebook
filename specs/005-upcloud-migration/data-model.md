# Data Model: Move Hosting from Azure to UpCloud

**No application schema change.** The EF model, tables and migrations stay as
they are; the data moves table for table (R9). This file describes the hosting
entities the spec lists and how they relate.

## Database service (shared, managed by UpCloud)

| Entity | Owner | Notes |
|---|---|---|
| Service `rdb.development.1CPU-1GB`, PG 18, `fi-hel1` | tracker's UpCloud account | private network only, TLS enforced, `max_connections` 50, daily backups kept 3 days |
| Database `gymnotebook` | role `gymnotebook` | `CONNECT` revoked from `PUBLIC`, granted to `gymnotebook` only |
| Role `gymnotebook` | — | login, no superuser, no `CREATEDB`/`CREATEROLE`; owner of its database so migrations can run |
| Tracker's database and role | tracker | `CONNECT` revoked from `PUBLIC` too (FR-004) |

**Connection budget** (FR-003): tracker ≤ 15 + Gym Notebook ≤ 10 = 25 of 50.

**Copy check** (`deploy/bin/verify-copy.sql`): one row per application table,
`table_name, row_count`, ordered by name, plus the last
`__EFMigrationsHistory.migration_id`. Run on Neon at the freeze and on UpCloud
after the restore. The two outputs must be byte-identical.

## Server (shared)

| Path | Owner, mode | Written by | Holds |
|---|---|---|---|
| `/opt/gymnotebook/` | `root:root 0755` | admin, from this repo's `deploy/` | `compose.prod.yml`, `bin/` |
| `/opt/edge/sites/gymnotebook.caddy` | `root:root 0644` | admin | our site blocks, read by the tracker's Caddy |
| `/etc/gymnotebook/.env` | `root:gymdeploy 0640` | admin, by hand | every variable in [server-env](contracts/server-env.md) |
| `/etc/gymnotebook/upcloud-ca.pem` | `root:gymdeploy 0640` | admin | the service CA, mounted read-only into `gym-api` |
| `/var/lib/gymnotebook/image-tag.env` | `gymdeploy` | `deploy.sh` only | `IMAGE_TAG=main-<40 hex>` |
| `/var/lib/gymnotebook/previous-tag` | `gymdeploy` | `deploy.sh` only | rollback target |

| User | Groups | Login |
|---|---|---|
| `gymdeploy` | `gymdeploy`, `docker` | key only, `restrict,command="/opt/gymnotebook/bin/deploy.sh"` |
| `admin`, `deploy` (tracker's) | unchanged; `admin` also joins `gymdeploy` to read our env file | unchanged |

## Containers

| Service | Image | Networks | Ports | Limits | Restart |
|---|---|---|---|---|---|
| `gym-api` | `ghcr.io/juusimaa/gymnotebook-backend:${IMAGE_TAG}` | `default`, `edge` | none | 512 MB, 0.75 CPU | `unless-stopped` |
| `gym-web` | `ghcr.io/juusimaa/gymnotebook-frontend:${IMAGE_TAG}` | `default`, `edge` | none | 64 MB, 0.25 CPU | `unless-stopped` |

## State transitions: cutover

```text
Azure+Neon live ──freeze (Neon read-only)──▶ frozen ──copy+verify ok──▶ copied
copied ──DNS apex → server, smoke ok──▶ UpCloud live ──final export archived──▶ Azure+Neon deleted
copied/UpCloud live ──smoke fails──▶ DNS back, Neon read-write ──▶ Azure+Neon live
```
