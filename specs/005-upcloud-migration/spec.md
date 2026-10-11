# Feature Specification: Move Hosting from Azure to UpCloud

**Feature Branch**: `005-upcloud-migration`

**Created**: 2026-10-11

**Status**: Draft for owner review. Nothing here is approved for implementation until the owner merges this spec and its plan (Principle VII).

**Input**: "Move from Azure to UpCloud. See Subscription Tracker project for reference. No need plan cutover much since I'm the only user currently. Important is to figure out can we host this in the UpCloud using same resources without extra cost."

**Reference**: `docker-subscription-tracker/specs/001-upcloud-migration/` and its `deploy/` directory. Subscription Tracker made this move on 2026-10-10/11 and is live on UpCloud. This spec reuses its decisions and only restates what is different for Gym Notebook.

## Context

Production today is split across three providers. The API and the web app run
on Azure Container Apps in Sweden, and the database is Neon's free tier in
Frankfurt. DNS is at Cloudflare in DNS-only mode, email goes through Resend's
EU region, and the bot check is Cloudflare Turnstile. All three of those stay.

Subscription Tracker already pays for two UpCloud resources in `fi-hel1`
(Helsinki):

| Resource | Plan | €/month excl. VAT | What it runs today |
|---|---|---|---|
| Server | Starter `1xCPU-2GB`, 20 GB disk, 0.5 TB fair-use egress | 6.00 | Caddy (TLS, proxy), Python API, nginx frontend |
| Managed PostgreSQL 18 | `rdb.development.1CPU-1GB`, 10 GiB storage, 1 node, daily backups kept 3 days, `max_connections` 50 | 9.40 | the tracker's one database |
| SDN private network + router, firewall | — | 0.00 | server ↔ database |

Both are fixed-price. Neither is close to full: the tracker has 5 users and
under 0.5 GB of data, and its disk had 3.2 GB used after first start.

The move puts Gym Notebook on **those same resources**: its containers on the
same server, behind the same edge, and its data in its own database inside the
same managed PostgreSQL service. If that fits, the move costs nothing extra
and removes the Azure bill. The owner is the only user, so the cutover can be
short and simple.

### Does it fit? (what is known now, what the plan must measure)

| Limit | Shared by | Known | Expected Gym Notebook need | Open question for the plan |
|---|---|---|---|---|
| Server memory, 2 GB | both stacks and the OS | tracker stack's use not yet recorded | one ASP.NET Core API and one nginx; a .NET API at this load typically sits in the low hundreds of MB | measure the tracker's live use; measure the gym API's idle and peak use; decide whether the .NET heap needs a cap |
| Server CPU, 1 vCPU | both stacks | load is a handful of users | same | confirm a gym export/restore of the 100,000-set reference does not starve the tracker |
| Server disk, 20 GB | images, logs | 3.2 GB used | two more images, under 1 GB together | confirm image pruning covers both stacks |
| Database connections, 50 | both apps, maintenance, backups | tracker pool about 15 | Npgsql's default pool maximum is 100, more than the whole service allows | set an explicit pool maximum so the two apps together stay well below 50 |
| Database storage, 10 GiB | both databases | tracker under 0.5 GB | gym data is small today | confirm current Neon size; the alert threshold must count both databases |
| Database memory, 1 GB | both apps | — | light | none expected |
| Egress, 0.5 TB/month | both | small | small | none expected |
| PostgreSQL major version | both | the service runs **18** | Gym Notebook targets **17** (tests, Compose, constitution) | move tests and Compose to 18; Neon's actual version decides how the copy is made |

If any limit does not fit, the plan MUST say so, with the cheapest option,
**before** anything is moved or bought (FR-002).

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The notebook is all there after the move (Priority: P1)

The owner opens `gymnotebook.fit` after the move. Every workout page,
exercise, block, set and the e1RM chart are as they were. They sign in with
the same email and password, and backup and restore still work.

**Why this priority**: a move that loses data or breaks sign-in fails no
matter what it saves.

**Independent Test**: before the move, take a JSON backup from the app and
record per-table row counts. After the move, compare the row counts, sign in,
open the chart for a few exercises, and run signup → confirm → sign in →
password reset with a second address.

**Acceptance Scenarios**:

1. **Given** the data in the Neon database, **When** the owner signs in on
   the new hosting, **Then** every table has the same row count and the
   owner's notebook, chart and last-backup date are unchanged.
2. **Given** the new hosting is live, **When** a new visitor signs up,
   **Then** the confirmation email arrives, the link works, and Turnstile,
   the email caps and the rate limits behave as before.
3. **Given** the new hosting is live, **When** anyone visits the site or its
   API, **Then** they get a valid HTTPS connection with no certificate
   warning.

---

### User Story 2 - Subscription Tracker is not affected (Priority: P1)

The tracker's users keep using `subscriptionstrack.com` during and after the
move without noticing anything. A broken Gym Notebook deploy, a crash or a
heavy export does not take the tracker down, and the reverse holds too.

**Why this priority**: the tracker has real users; Gym Notebook has one.
Saving the Azure bill is not worth an outage on the tracker.

**Independent Test**: during the rehearsal, deploy a Gym Notebook image that
fails its health check and stop the gym API container; check that the
tracker's site, API and UptimeRobot monitors stay green throughout. Run the
100,000-set export while timing tracker requests.

**Acceptance Scenarios**:

1. **Given** both apps run on the server, **When** a Gym Notebook deploy
   fails its health check, **Then** the previous Gym Notebook version keeps
   serving and the tracker is untouched.
2. **Given** both apps share the database service, **When** Gym Notebook
   opens as many connections as it is allowed, **Then** the tracker can still
   connect.
3. **Given** both apps share the database service, **When** either app's
   database credentials are used, **Then** they can read and write only that
   app's database.

---

### User Story 3 - Merges keep deploying themselves (Priority: P2)

The owner merges a PR to `main`, and the new version reaches
`gymnotebook.fit` with no manual step, as it does today on Azure. A version
that fails its health check never replaces the running one.

**Why this priority**: there is one maintainer, and `main` deploys itself
today. Manual deploys would be a step back.

**Independent Test**: merge a trivial change and watch it go live. Then
deploy an image that fails its health check and see the old one keep
serving.

**Acceptance Scenarios**:

1. **Given** a merge to `main`, **When** the image build finishes, **Then**
   the new version is serving within 15 minutes with no manual step.
2. **Given** a new version that fails its health check, **When** the deploy
   runs, **Then** the previous version keeps serving and the deploy is
   reported as failed.

---

### User Story 4 - Azure and Neon are gone (Priority: P3)

Once the new hosting works, the Azure resources, the Azure deploy
credentials, the Bicep templates and the Neon project are removed, and the
docs describe UpCloud as production.

**Why this priority**: removing Azure is the cost saving. It can only happen
after the move works, but there is no reason to keep both for long with one
user.

**Independent Test**: the Azure resource group and Neon project no longer
exist; a repository search for the old hostnames, `infra/` and Azure deploy
steps finds only historical notes.

**Acceptance Scenarios**:

1. **Given** the new hosting has passed its smoke tests, **When** the owner
   decommissions the old hosting, **Then** a final Neon export has been
   archived and checked first, and the Azure resources, Azure OIDC app
   registration and GitHub secrets, and the Neon project are deleted.
2. **Given** decommissioning is done, **When** someone reads the README,
   `PLAN.md` and the workflows, **Then** they describe UpCloud as production.

---

### Edge Cases

- **Writes during the copy**: the owner is the only user, so the freeze can
  be "the owner does not log a session while copying", backed by stopping
  the Azure apps. No maintenance page or announced window is required.
- **Shared edge ports**: only one process can own ports 80 and 443. Both
  apps' hostnames must be served by one edge, and a configuration change for
  one app's hostnames must not break the other's TLS or routing.
- **Real client address**: rate limits must stay keyed on the visitor's real
  address behind the new proxy, and a client must not be able to choose its
  own key (today's `FORWARDED_HEADERS_ENABLED` rule, PLAN.md § rate limiting).
- **Memory pressure**: if the server runs low on memory, one app must not be
  able to take the other down with it, and the existing memory alert must
  fire.
- **Database maintenance or restart**: `/health` reports unhealthy, requests
  fail fast rather than hang, and both apps recover on their own.
- **Restoring one app's data**: UpCloud restores a whole service into a new
  one. Restoring Gym Notebook from a provider backup must not roll back the
  tracker's data.
- **Removing Gym Notebook later**: its containers, edge entries and database
  can be removed without stopping the tracker.
- **Server reboot**: after an automatic security reboot, both apps come back
  with no manual step.
- **Existing sessions and emailed links**: a session or a confirmation/reset
  link issued on Azure keeps working after the move, as long as it hasn't
  expired.

## Requirements *(mandatory)*

### Functional Requirements

**Cost and capacity**

- **FR-001**: Gym Notebook MUST run on Subscription Tracker's existing
  UpCloud server and managed PostgreSQL service. The UpCloud monthly bill
  MUST NOT increase because of it: no plan upgrade, no extra storage, no new
  billed resource.
- **FR-002**: Before any change to production, the plan MUST record the
  measured fit against every limit in § Does it fit: server memory, CPU,
  disk, database connections, storage and version. If a limit does not fit,
  the plan MUST stop and give the cheapest option and its monthly price for
  the owner to decide.
- **FR-003**: Together, both apps' maximum database connections MUST leave
  room within the service's limit for maintenance and backup connections.

**Isolation from Subscription Tracker**

- **FR-004**: Gym Notebook's data MUST live in its own database with its own
  database user. Neither app's user may read or write the other's database.
- **FR-005**: A Gym Notebook deploy, failure or restart MUST NOT restart,
  stop or reconfigure the tracker's containers, and vice versa. Shared
  pieces (the edge, server setup, alerting) MUST have one owner, and the
  plan MUST say which repository owns them.
- **FR-006**: Gym Notebook's deploy credential MUST be able to deploy only
  Gym Notebook. It MUST NOT give a shell, control of the tracker, or control
  of the UpCloud account.
- **FR-007**: Gym Notebook's secrets MUST be stored on the server apart from
  the tracker's, readable only by its own deploy process and app, and never
  in the repository or images.

**Data and cutover**

- **FR-008**: All production data MUST move from Neon to the new database
  with nothing lost or changed, checked by per-table row counts before DNS
  switches.
- **FR-009**: Production, CI tests and local Compose MUST use the same
  PostgreSQL major version as the managed service (18). This changes the
  constitution's stated version (Principle I, PostgreSQL 17) and its
  alignment rule (Principle IV); the amendment goes in the same PR.
- **FR-010**: The JWT signing key MUST be copied unchanged, so existing
  sessions and emailed links keep working. Other secrets (Resend,
  Turnstile) are reused unchanged.
- **FR-011**: The cutover MUST follow a short written checklist: stop
  writes, copy, check counts, switch DNS, smoke test, and how to go back to
  Azure + Neon if the smoke test fails. No maintenance page or announced
  window is required.

**Hosting and behavior**

- **FR-012**: The site MUST stay at `https://gymnotebook.fit`, with
  certificates that renew themselves. The API MUST get a stable hostname
  under `gymnotebook.fit` instead of the `azurecontainerapps.io` one, so
  the provider can change without the browser config changing again.
- **FR-013**: All current production behavior MUST be preserved: email
  confirmation and reset, Turnstile, email caps, CORS, rate limits,
  anti-enumeration answers, the backup body limit and timeouts.
- **FR-014**: Per-client rate limits MUST be keyed on the visitor's real
  address behind the new proxy, and a client MUST NOT be able to spoof it.
  The API container MUST NOT be reachable except through the edge.
- **FR-015**: The database MUST only be reachable over the private network,
  with encrypted connections, as the tracker's is.
- **FR-016**: Neither the API nor the frontend may scale to zero or idle
  out. Both MUST come back by themselves after a reboot or crash.

**Deploy and operations**

- **FR-017**: Every merge to `main` MUST deploy automatically to the new
  hosting, pinned to the exact image tag CI built. A version that fails its
  health check MUST NOT replace the running one.
- **FR-018**: The maintainer MUST be alerted when `gymnotebook.fit` or its
  API has been unreachable for 5 minutes, using the free monitoring the
  tracker already uses.
- **FR-019**: The existing memory, disk and database-storage alerts MUST
  account for both apps: the storage alert MUST measure the whole service,
  not only the tracker's database.
- **FR-020**: A restore procedure for Gym Notebook's database MUST be
  written. It restores Gym Notebook only and leaves the tracker's live data
  untouched. Backup retention is the provider's 3 days, as for the tracker;
  the in-app JSON backup (spec 004) remains the user's own long-term copy.

**Decommissioning and documentation**

- **FR-021**: After the smoke tests pass, Azure resources, the Azure OIDC
  app registration and its GitHub secrets, the Neon project and `infra/`
  MUST be removed. A final Neon export MUST be archived and checked first.
  No soak period is required (the owner is the only user).
- **FR-022**: `README.md`, `PLAN.md` (stack, configuration table, rate
  limiting, milestones), the deploy workflow and code comments that name
  Azure, Container Apps or Neon MUST describe the new hosting in the PR
  that makes it production.
- **FR-023**: The privacy supplier inventory (`docs/privacy/suppliers.md`),
  processing decision and notice candidate MUST be updated for the change of
  processors and data location (Sweden/Frankfurt → Helsinki, Finland) before
  cutover. Any change the processing decision requires the owner to sign
  MUST be signed first.
- **FR-024**: Required CI checks MUST keep reporting on every PR throughout
  the move.

### Key Entities

- **Shared server**: the tracker's UpCloud server. Gains Gym Notebook's API
  and frontend containers and its hostnames at the edge.
- **Shared database service**: the tracker's managed PostgreSQL 18 service.
  Gains a separate Gym Notebook database and user.
- **Edge**: the single reverse proxy that owns ports 80/443 and holds TLS
  certificates for both apps' hostnames.
- **Gym Notebook deploy credential**: a key that can only roll Gym Notebook
  to a given image tag.
- **Cutover checklist**: the short ordered list in FR-011.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The UpCloud bill for the first full month after the move is
  the same as the month before (€15.40 excl. VAT, ±€0.30 for month length),
  and the Azure and Neon bills for this app are zero.
- **SC-002**: After the move, every table's row count matches Neon's at the
  freeze, and the owner signs in with their existing password.
- **SC-003**: Over the first 30 days, the tracker has no downtime caused by
  Gym Notebook, and its availability stays at or above its own 99.5%
  target.
- **SC-004**: A merged PR is live on `gymnotebook.fit` within 15 minutes,
  with no manual step.
- **SC-005**: After 2 hours of no traffic, the first page load and the
  first sign-in are as fast as warm ones (nothing scales to zero).
- **SC-006**: With both apps idle, the server has at least 30% of its memory
  free; during the 100,000-set export it stays below the 80% memory alert.
- **SC-007**: Within 7 days of cutover, no Azure or Neon resource tied to
  this app exists.

## Assumptions

- **Co-hosting is the goal, not a new server.** The owner wants the existing
  resources reused. A separate server and database would add at least
  €15.40/month and is out of scope unless FR-002 finds that sharing does not
  fit.
- **API hostname**: `api.gymnotebook.fit`, following the tracker's
  `api.subscriptionstrack.com`. The frontend keeps reading it from its
  runtime `config.js`, and `CORS_ORIGINS` stays `https://gymnotebook.fit`.
  Confirmed by the owner on 2026-10-11 (plan D3).
- **DNS stays at Cloudflare in DNS-only mode.** The A records change to the
  server's address. Turning on Cloudflare's proxy would change the rate-limit
  key and the privacy inventory, so it stays off.
- **Images** keep being built by the existing `build-and-push.yml` and
  published to GHCR. Only where they run changes.
- **Backups**: the tracker accepted 3 days of provider backups to keep cost
  down; Gym Notebook inherits the same, and the owner keeps app JSON backups.
- **Cutover risk is low**: one user and small data. A short freeze by the
  owner is enough, and rolling back means pointing DNS back to Azure, which
  stays untouched until the smoke tests pass.
- **Privacy feature flag** remains off in production; this move changes
  processors and locations, not what is processed.
- **Out of scope**: a separate UpCloud server or database, high
  availability, a CDN, moving DNS or email providers, a take-down/pause
  runbook (Gym Notebook adds no billed resource, so taking it down would
  save nothing), and any app behavior change beyond what hosting requires.
