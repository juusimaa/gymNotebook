# Implementation Plan: Demo Mode

**Branch**: `007-demo-mode` | **Date**: 2026-10-16 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `specs/007-demo-mode/spec.md`

**Status**: Draft for owner review (Principle VII).

## Summary

A visitor can start a 24-hour demo account from the sign-in screen and get a notebook filled with about twelve weeks of realistic training. The demo is an ordinary `User` row with a `demo_expires_at` (research R1), so every screen runs against the real API.

- **Seed**: a pure generator (`DemoSeed`, R4) builds a format-2 `RestoreFile`. It is inserted through restore's core (`NotebookRestore.ImportAsync`, R5), so validation and optional-details consent work exactly as for a backup.
- **Reset**: replaces the notebook under exclusive account access (R6).
- **Expiry**: enforced by the existing token checks (R2). A background worker deletes expired demos through `AccountDeletion` (R8).
- **Off by default**: the feature sits behind `DEMO_MODE_ENABLED`, Turnstile, a per-IP limit and a cap on live demos (R3).

PR 1 (this plan's tasks) is the backend plus `PLAN.md`/`README.md`. PR 2 is the screens and `docs/ui/`.

## Technical Context

**Language/Version**: C# / .NET 10 (backend); TypeScript, React, Vite (PR 2)

**Primary Dependencies**: ASP.NET Core Minimal APIs, EF Core 10 + Npgsql, BCrypt.Net, the JWT bearer handler. No new packages.

**Storage**: PostgreSQL 18. One new nullable column, `users.demo_expires_at`, with a partial index ([data-model.md](data-model.md)).

**Testing**: xUnit + WebApplicationFactory + Testcontainers Postgres 18; pure unit tests for `DemoSeed`.

**Target Platform**: Linux containers (Compose locally, the UpCloud server in production)

**Project Type**: Web service + SPA

**Performance Goals**: A demo start (create, seed about 40 pages and 600 sets, mint a token) and a reset each finish in under 2 s locally, within SC-001 (10 s) and SC-003 (5 s).

**Constraints**: The start is one transaction. The reset holds the exclusive lock for less than `Lifecycle:ExclusiveLockTimeoutMs`. No email can be queued for a demo account.

**Scale/Scope**: At most `Demo:MaxLiveAccounts` (default 200) live demos, about 600 sets each, so about 120 k rows at the cap. That is negligible next to the 100 k-set reference notebook the deletion tests use.

## Constitution Check

*GATE: checked before Phase 0 and again after Phase 1. Constitution 3.2.0.*

| Principle | Check | Status |
|-----------|-------|--------|
| I. Architecture | Minimal API routes in a new `DemoEndpoints.cs` (like `BackupEndpoints.cs`). No layers, interfaces or packages. Reuses restore, deletion, Turnstile and the lifecycle lock. | Pass |
| II. Focused changes | Two PRs. PR 1's refactors are limited to what reuse needs: `ImportAsync` split out of restore, and one shared delete method in `AccountDeletion`. | Pass |
| III. Readability | The generator is plain code with comments explaining the training logic. Every non-obvious choice (the no-`@` address, the global lock, the exclusive reset) gets a comment pointing at its research entry. | Pass |
| IV. Verification | Real-Postgres tests for start, cap, rate limit, flag-off 404, reset, ownership, expiry, cleanup, refusals and lock coverage. Unit tests for the seed's invariants over many seeds. Migration reviewed before applying. | Pass |
| V. Security & privacy | The 3.2.0 amendment covers demo accounts. Turnstile fails closed, a per-IP limit applies, and live demos are capped. Ownership is unchanged (404s). No secrets or tokens are logged. The privacy notice and consent flows run unchanged. Demo addresses can't receive email (R7). | Pass (amendment 3.2.0, owner-approved 2026-10-16) |
| VI. Docs | PR 1 updates `PLAN.md` (data model, API, Auth, Milestones), `README.md` (configuration) and `docker-compose.yml` (flag pass-through). PR 2 updates `docs/ui/` with the screens. | Pass |
| VII. Review | This plan and the tasks are drafts until the owner approves them. | Pending owner review |

Post-design re-check: no new violations. Complexity Tracking is empty.

## Project Structure

### Documentation (this feature)

```text
specs/007-demo-mode/
├── spec.md
├── plan.md              # this file
├── research.md          # R1–R10
├── data-model.md
├── quickstart.md
├── contracts/api.md
├── checklists/requirements.md
└── tasks.md             # /speckit-tasks
```

### Source Code

```text
backend/GymNotebook.Api/
├── User.cs                    # + DemoExpiresAt
├── Data/AppDbContext.cs       # + partial index
├── Migrations/…_AddDemoAccounts.cs
├── DemoOptions.cs             # new: Demo:* settings, validated at boot
├── DemoSeed.cs                # new: pure generator → RestoreFile (R4)
├── DemoEndpoints.cs           # new: POST /auth/demo, POST /account/demo/reset (R3, R6)
├── DemoCleanupWorker.cs       # new: BackgroundService (R8)
├── DemoStartRequest.cs, DemoResetRequest.cs   # new request records
├── NotebookRestore.cs         # ImportAsync split out (R5)
├── AccountDeletion.cs         # DeleteExpiredDemoAsync + shared delete method; demo refusal (R7, R8)
├── AccountLifecycle.cs        # shared check skips expired demos (R2)
├── NotebookExport.cs          # demo refusal (R7)
├── MeResponse.cs              # + DemoExpiresAt (R9)
├── Turnstile.cs               # + DemoAction
└── Program.cs                 # flag, options, "demo" rate limit, OnTokenValidated expiry,
                               # login/change-password refusals, /me field, map + worker

backend/GymNotebook.Tests/
├── DemoSeedTests.cs                 # unit: invariants, coverage, dates, determinism
├── DemoStartTests.cs                # start, flag off, captcha, cap, rate limit, /me
├── DemoResetTests.cs                # reset, consent-dependent details, refusals, ownership
├── DemoExpiryTests.cs               # expired token 401, cleanup deletes, registered untouched
├── DemoRefusalTests.cs              # login, change-password, export, delete
├── DemoEnabledGymNotebookFactory.cs # flag on (+ privacy-on variant where needed)
└── LifecycleCoverageTests.cs        # + allow-list entry for the reset
```

**Structure Decision**: The existing two-project backend (API + tests), as in `PLAN.md` → Folder structure. The demo code gets its own files because it is one cohesive feature, the same way `BackupEndpoints.cs` and `PrivacyEndpoints.cs` keep theirs out of `Program.cs`.

## Open questions for review

None block implementation. These defaults are worth a glance, and all are settings:

- the 24 h lifetime;
- 3 starts per IP per hour;
- 200 live demos;
- a 15-minute cleanup interval.

## Complexity Tracking

No violations to justify.
