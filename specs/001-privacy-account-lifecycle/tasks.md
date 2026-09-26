---

description: "Task list for the Privacy and Account Lifecycle feature"
---

# Tasks: Privacy and Account Lifecycle

**Input**: Design documents from `/specs/001-privacy-account-lifecycle/`
**Prerequisites**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md), [checklists/proposal-review.md](checklists/proposal-review.md)

**Status**: Generated 2026-09-24; reviewed and approved by the owner for implementation, 2026-09-25 (constitution Principle VII). Per Principle III (constitution 2.0.0, 2026-09-25), AI implements these tasks by default unless the owner says they will write a part; the owner reviews every PR. The delegation notes below predate that change and are kept as a record.

**Tests**: Required. The spec defines an Independent Test per story and SC-003–SC-006, and constitution Principle IV requires real-PostgreSQL integration tests. Write each story's tests first and confirm they fail before implementing.

**Organization**: Tasks are grouped by user story. All five stories are P1 and are ordered as in spec.md.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependency on an incomplete task)
- **[Story]**: The user story the task serves (US1–US5)
- **(owner)** / **(operator)**: The task is a human decision, a review or an operational action, not code
- **Release gate**: Must be complete before `PRIVACY_LIFECYCLE_ENABLED` is set to `true` in production

## Path Conventions

- Backend API: `backend/GymNotebook.Api/` (Minimal APIs, EF entities, `Data/AppDbContext.cs`, `Migrations/`)
- Backend tests: `backend/GymNotebook.Tests/` (xUnit, `GymNotebookFactory` with Testcontainers PostgreSQL 17)
- Frontend: `frontend/src/` (`api/`, `auth/`, `screens/`, `styles/`, `routes.tsx`)
- Operating records: `docs/privacy/`; UI specification: `docs/ui/`; infrastructure: `infra/`

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: Changes that do not depend on the feature's design, and the production disablement switch that must exist before any feature code merges to `main`.

- [x] T001 [P] Self-host fonts as a **separate PR branched from `main`** (P28, research R8):
  - Fetch the Latin-subset `.woff2` files for Cormorant Garamond 400/600 and Lora 400, 500 and italic 400, plus the SIL OFL 1.1 license text, into `frontend/public/fonts/`.
  - Add `@font-face` rules in `frontend/src/styles/tokens.css`.
  - Remove the three Google `<link>` tags from `frontend/index.html`.
  - Verify in the browser network panel that no `fonts.googleapis.com`/`fonts.gstatic.com` request remains.
  - **Done 2026-09-25:** merged in PR #48.
- [x] T002 [P] Turn off the frontend nginx access log as a **separate PR branched from `main`** (analysis C1, FR-019):
  - Add `access_log off;` to `frontend/nginx.conf`, keeping `error_log`, with a comment on why a static SPA needs no identifying access log.
  - Land it early: stored lines (remote address, user agent, path) only age out about 30–31 days after deployment (research R7).
  - T075 verifies the age-out before the flag is enabled.
  - **Done 2026-09-25:** merged in PR #47; stored lines age out by about 2026-10-26.
- [x] T003 Read `PRIVACY_LIFECYCLE_ENABLED` at startup next to `INVITE_CODE` in `backend/GymNotebook.Api/Program.cs`. Only the exact value `true` enables the feature; unset, empty or any other value disables it (fail closed, P25). Comment why this deliberately differs from `INVITE_CODE`.
- [x] T004 [P] Add `PRIVACY_LIFECYCLE_ENABLED` to `.env.example` (value `false`) and to the backend service in `docker-compose.yml`.
- [x] T005 [P] Add `PRIVACY_LIFECYCLE_ENABLED` as a plain (non-secret) environment value set to `false` in `infra/modules/container-app-api.bicep`.
- [x] T006 [P] Add `PrivacyEnabledGymNotebookFactory` to `backend/GymNotebook.Tests/PrivacyEnabledGymNotebookFactory.cs`, following the `InviteCodeGymNotebookFactory` pattern and setting `PRIVACY_LIFECYCLE_ENABLED=true`.
- [x] T007 Document `PRIVACY_LIFECYCLE_ENABLED` (purpose, fail-closed default, how to enable locally) in the Configuration section of `README.md`.

**Delegation (owner, 2026-09-25, constitution Principle III):** AI implemented T003–T007 on `feat/001-privacy-lifecycle-flag`, as explicitly requested. The scope is only the flag, its configuration and documentation, and the test factory. The base `GymNotebookFactory` also pins the flag to `false`, the same way it pins `INVITE_CODE`. The delegation does not extend to Phase 2 or later tasks.

**Checkpoint**: The flag exists and defaults to off everywhere; T001 and T002 can merge independently.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: Account schema additions and R4 lifecycle coordination on existing routes. US1, US3 and US4 depend on these; they ship unflagged (P25).

**⚠️ CRITICAL**: Do not implement the endpoint filter (T019) before the Part A spike (T008) has passed and the Q4 values are recorded.

### Validation spike (Q7)

- [x] T008 Run spike Part A locally on a `spike/r4-cancellation` branch, per research R4 → Validation spike, scenarios A1–A6:
  - Use two app hosts on one Testcontainers PostgreSQL.
  - Check A1 against its pass criterion: p95 increase ≤ 10 ms locally at 20 concurrent clients, and the pool never exhausted.
  - Record the results, and any tuned Q4 values, in the Validation spike section of `specs/001-privacy-account-lifecycle/research.md`.
  - Spike code is not merged; A1–A5 tests may be kept for T012/T013.
  - **Done 2026-09-25:** passed; results and findings in research R4 → Part A results.
  - **Delegated to AI (owner, 2026-09-25):** an explicit exception to this file's default that the author writes the code by hand. It covers the spike only, not the tasks that later reuse its code (research R4 → Implementation delegation).
- [x] T009 (owner) Approve or decline spike Part B's disposable Azure environment (research R4 → Part B), and record the decision in `specs/001-privacy-account-lifecycle/research.md`. Part B itself runs in T053 (US3). **Done 2026-09-25:** approved with conditions (research R4 → Part B).

### Tests for the foundation (write first; they must fail)

- [x] T010 [P] Add a two-host fixture sharing one PostgreSQL container, for concurrency tests, in `backend/GymNotebook.Tests/TwoHostGymNotebookFixture.cs`.
- [x] T011 [P] Add a coverage test in `backend/GymNotebook.Tests/LifecycleCoverageTests.cs`. It enumerates the endpoint data source and fails if any endpoint requiring authorization lacks the lifecycle filter, except for the explicit allow-list, each entry carrying a documented reason: `POST /account/export` (own snapshot guards), `POST /auth/change-password` and `POST /account/delete` (own exclusive transaction, research R4 → analysis I1).
- [x] T012 [P] Add read-coordination tests in `backend/GymNotebook.Tests/LifecycleCoordinationTests.cs`:
  - A read holds shared access through its response write.
  - A request waiting behind a committed deletion gets 401.
  - A shared-lock wait over 5 s gets 503 `temporarily_unavailable` with `Retry-After`.
  - Use explicit barriers, not sleeps.
- [x] T013 [P] Add write-coordination tests in `backend/GymNotebook.Tests/LifecycleWriteDeliveryTests.cs`:
  - A write that commits and whose delivery guard then fails (deletion or password change first) gets 401 with no body.
  - PUT `/workouts/{id}/exercises` and POST `/workouts/{id}/sets` still roll back whole on failure inside the filter's transaction.
- [x] T014 [P] Add login race tests in `backend/GymNotebook.Tests/LoginRaceTests.cs` (Q6). A token issued while a deletion commits, and a token issued while a password change commits, each get 401 on their first guarded request.
- [x] T015 [P] Add suspension tests in `backend/GymNotebook.Tests/SuspensionTests.cs` (P3, Q5):
  - Wrong password on a suspended account gets the existing 401.
  - Correct password gets 403 `{ "code": "account_suspended" }`.
  - A token for a suspended account is rejected by `OnTokenValidated`.
- [x] T016 [P] Add migration and registration tests in `backend/GymNotebook.Tests/AccountIdentityTests.cs`:
  - Existing users are backfilled with distinct UUIDs and null acknowledgement.
  - New registration generates a UUID server-side.
  - A reused username gets a different `PrivacyAccountId`.

### Implementation for the foundation

- [x] T017 Add the User fields to `backend/GymNotebook.Api/User.cs` and configure them in `backend/GymNotebook.Api/Data/AppDbContext.cs`. Quoted constraints from data-model.md:
  - `PrivacyAccountId`: "UUID, non-null, unique, immutable, server generated".
  - `AcknowledgedPrivacyNoticeVersion`: "Nullable bounded string, proposed max 64".
  - `PrivacyNoticeAcknowledgedAt`: "Nullable timestamptz".
  - `SignInSuspendedAt`: "Nullable timestamptz".
  - "Both acknowledgement fields are null or both populated."
- [x] T018 Generate the migration with `dotnet ef migrations add` in `backend/GymNotebook.Api/Migrations/`:
  - Backfill genuine UUIDs for existing rows with uniqueness enforced.
  - Never seed acknowledgement.
  - Review the generated migration for unrelated schema changes before applying it (depends on T017).
- [x] T019 Implement the concrete lifecycle endpoint filter and guards in `backend/GymNotebook.Api/AccountLifecycle.cs`, with no interface (research R4 → Q3):
  - Begin a transaction on the scoped `AppDbContext`, set `lock_timeout` to 5 s with `SET LOCAL` (production goes through the Neon pooler, research R4 → Connection pooling), and take `pg_advisory_xact_lock_shared(<lifecycle namespace>, userId)` followed by the token-version/existence check, using the lock-and-check variant that passed spike A6 (not necessarily one statement).
  - For reads, write the result under the lock within a 10 s write timeout, then commit.
  - For writes, commit, then write the response under a fresh delivery guard.
  - Commit only when the handler returns a 2xx; any other result rolls back (spike A5).
  - Keep the write timeout below the exclusive wait: it is the only bound on a stalled read's shared hold (spike A3).
  - Map outcomes per Q5 (503/401). Comment the lock namespaces and why writes use two steps (depends on T008, T018).
- [x] T020 Attach the filter to the `/exercises` and `/workouts` groups and to `/auth/me` in `backend/GymNotebook.Api/Program.cs`. Do **not** attach it to `/auth/change-password`: exclusive endpoints own their transaction (analysis I1) (depends on T019).
- [x] T021 Remove the explicit `BeginTransactionAsync` calls from PUT `/workouts/{id}/exercises` and POST `/workouts/{id}/sets` in `backend/GymNotebook.Api/Program.cs`. Keep their intermediate `SaveChangesAsync` calls, and update the comments explaining that the filter now owns the transaction (depends on T020).
- [x] T022 Add the concrete exclusive-guard helper (for example `AcquireExclusiveAsync`, 15 s wait, returning 503 `temporarily_unavailable` on timeout) to `backend/GymNotebook.Api/AccountLifecycle.cs`. Change `/auth/change-password` in `backend/GymNotebook.Api/Program.cs` to own its transaction through that helper, never while holding the shared filter lock. After commit, deliver the new token under a fresh shared guard that validates the new token version, suppressing a stale token if a deletion or another change wins (research R4) (depends on T019).
- [x] T023 Generate `PrivacyAccountId` server-side at registration in `backend/GymNotebook.Api/Program.cs` (depends on T017).
- [x] T024 Add the suspension behavior in `backend/GymNotebook.Api/Program.cs`:
  - In login, after BCrypt verification succeeds, return 403 `{ "code": "account_suspended" }` when `SignInSuspendedAt` is set.
  - In `OnTokenValidated`, reject a suspended account as a backstop, using the row it already loads (depends on T017).
- [x] T025 [P] Map 403 `account_suspended` at login to copy pointing to the privacy contact in `frontend/src/api/authErrors.ts`, and extend `frontend/src/api/authErrors.test.ts`.
- [x] T026 Add the Q2d invariant test to `backend/GymNotebook.Tests/AccountIdentityTests.cs`. It fails if any code path other than account deletion removes User rows, for example a source scan that allows only the deletion code to remove `Users` (depends on T016).
- [x] T027 Record the lifecycle coordination foundation in `PLAN.md` (a milestone/design entry) and note the per-request connection hold in `README.md` (Principle VI).

**Checkpoint**: All existing tests pass with the filter attached. T011–T016 and T026 pass. The migration has been reviewed.

**Done 2026-09-25** on `feat/001-lifecycle-foundation`: the full backend suite (115 tests, 22 of them new) passes with the filter attached, and the generated migration was reviewed. Its only hand edit is the `gen_random_uuid()` backfill, replacing EF's all-zero default, which would have broken the unique index.

---

## Phase 3: User Story 1 — Understand how my information is used (Priority: P1) 🎯 MVP

**Goal**: A public versioned notice, account privacy state, and a "Continue" gate before notebook access that records acknowledgement, never consent.

**Independent Test**: Open the notice signed out and signed in; compare its disclosures with the reviewed inventory; walk through first-visit, repeat-visit, new-version, abandoned-notice and cross-session cases (quickstart §1).

### Tests for User Story 1

- [x] T028 [P] [US1] Add notice API tests in `backend/GymNotebook.Tests/PrivacyNoticeTests.cs`:
  - `GET /privacy/notice` is public, has no side effects and returns 404 with the flag off.
  - These are the flag's first behavioural tests (T003 has none, because nothing reads the flag yet). Besides `"false"` (the base factory) and `"true"` (`PrivacyEnabledGymNotebookFactory`), also boot with near-miss values, at least `"True"` and `""`, and expect 404. This proves the exact-match, fail-closed rule (P25). Each value needs its own factory subclass, like `InviteCodeGymNotebookFactory`.
  - `GET /account/privacy` returns `requiresAcknowledgement` correctly for null and old acknowledgement.
  - `PUT /account/privacy/acknowledgement` accepts only the current version, returns 409 `notice_version_changed` for a stale one, is idempotent for the same version (preserving the timestamp) and stores no consent value.
  - An announced successor appears in `announcedSuccessor` before its effective date without changing `requiresAcknowledgement`. After activation, an account that acknowledged the prior version gets `requiresAcknowledgement: true` again (FR-003, quickstart §1.4).
- [x] T029 [P] [US1] Add Vitest tests for the notebook-gate decision helper and safe same-origin return-URL validation in `frontend/src/auth/noticeGate.test.ts`.

### Implementation for User Story 1

- [x] T030 [US1] (owner) Create two synthetic, clearly labelled development notice versions (a current one and an announced successor with a future `effectiveAt`) and a version index in `docs/privacy/notices/`, per data-model.md → PrivacyNoticeVersion. Superseded versions stay in the index for accountability (FR-003). Real publication content is gated on T041–T043.
- [x] T031 [US1] Implement notice loading from the versioned artifacts, as trusted server configuration and never client text, plus `GET /privacy/notice`, `GET /account/privacy` and `PUT /account/privacy/acknowledgement` in `backend/GymNotebook.Api/PrivacyEndpoints.cs`:
  - Map them only when `PRIVACY_LIFECYCLE_ENABLED` is true.
  - Attach the lifecycle filter to the account routes.
  - Use `Cache-Control: no-store` on personal responses.
  - Select the current version from the index pointer, and expose an announced successor's metadata until its `effectiveAt`, then switch the gate to it.
  - How the artifacts reach the API image is an implementation choice for owner review (depends on T020, T030).
- [x] T032 [P] [US1] Add the notice and privacy-state wire types and calls, treating a 404 from `GET /account/privacy` as "feature off", in `frontend/src/api/privacy.ts`.
- [x] T033 [P] [US1] Implement the notebook-gate helper and return-URL validation in `frontend/src/auth/noticeGate.ts` (makes T029 pass).
- [x] T034 [US1] Create the public notice screen `/privacy` in `frontend/src/screens/PrivacyNotice.tsx`, rendering structured text only and never unchecked HTML (depends on T032).
- [x] T035 [US1] Create the account privacy screen `/account/privacy`, with links to the notice, export, deletion and contact, in `frontend/src/screens/AccountPrivacy.tsx` (depends on T032).
- [x] T036 [US1] Create the notice gate `/account/privacy/notice` in `frontend/src/screens/NoticeGate.tsx` (depends on T032, T033):
  - "Continue" sends exactly the displayed version.
  - A 409 reloads the newer notice.
  - A network failure keeps the gate and offers retry.
  - Leaving does not acknowledge.
- [x] T037 [US1] Register the new routes in `frontend/src/routes.tsx`, and run the gate before any notebook fetch on "Open the notebook" and on all notebook deep links (workouts, progress, exercises, editors). Cover, account, privacy and change-password stay reachable (depends on T033–T036).
- [x] T038 [P] [US1] Add the "Privacy & account" entry to `frontend/src/screens/Cover.tsx`, hidden when the feature is off.
- [x] T039 [P] [US1] Add the public notice link to `frontend/src/screens/Login.tsx`, hidden when `GET /privacy/notice` returns 404.
- [x] T040 [US1] Align `docs/ui/README.md` and `docs/ui/prototype.html` with the implemented notice routes and states, and record US1 in `PLAN.md`.

**Checkpoint**: With the flag on locally, US1 passes quickstart §1 and T028–T029 pass. With the flag off, nothing new is visible.

**Done 2026-09-25** on `feat/001-us1-privacy-notice`:
- T028–T029 pass, and the full backend and frontend suites pass.
- **T030:** drafted by AI with the owner's approval. The versions are labelled synthetic; real content remains gated on T041–T043.
- **T031's open item:** the owner chose embedded resources, with the backend image built from the repository root (`backend/Dockerfile.dockerignore` allow-list).
- The flag-on API behaviour was smoke-tested against the Compose image. The owner still has to record the browser walkthrough of quickstart §1.

---

## Phase 4: User Story 2 — Establish whether consent is needed (Priority: P1)

**Goal**: A reviewed, purpose-by-purpose processing decision, including the health-data assessment and the consent conclusion.

**Independent Test**: Review the decision record against account information, workout/bodyweight information, free-text notes, security logs and browser storage; every purpose has a recorded conclusion and reviewer.

- [ ] T041 [US2] (owner) Write `docs/privacy/processing-decision.md`, per contracts/operations.md → Maintained artifacts:
  - Cover account administration, training/progress, free text/bodyweight, logs (including the deletion log lines, R6 Q2e), browser storage and discovered collection.
  - For each purpose, record necessity, lawful basis and rationale, the health-data assessment with any additional condition (FR-005), and the consent conclusion.
  - Record the reviewer, date and evidence.
  - **Drafted 2026-09-25** on `docs/001-us2-processing-decision`: facts from the code and R7, with proposed conclusions awaiting the owner's decision and signature. P3 (free text and bodyweight): the owner chose option B, explicit consent, on 2026-09-25, which triggers T042.
- [ ] T042 [US2] (owner) **Release gate.** If any purpose concludes that consent is required, stop that processing's rollout and raise a specification amendment under FR-007. Record the outcome in `docs/privacy/processing-decision.md`.
  - **Outcome recorded 2026-09-25:** consent is required for P3 (bodyweight, title, location, notes), so that processing is halted until an FR-007 amendment is approved and implemented. Exercise names are still an open point.
- [ ] T043 [US2] (owner) **Release gate.** Supply the controller identity, monitored privacy contact and supervisory authority (Q8), and write the reviewed notice content that replaces the synthetic version in `docs/privacy/notices/`. No placeholders may be published (FR-002, FR-025).

**Checkpoint**: Every purpose has a dated conclusion, and no unresolved consent finding remains for released processing.

---

## Phase 5: User Story 3 — Take a copy of my notebook (Priority: P1)

**Goal**: A password-verified, streamed, single-snapshot JSON export with embedded field explanations.

**Independent Test**: Export a seeded account with every field, an unused exercise, repeated blocks and unfinished workouts; compare field by field; confirm another account's data is absent (quickstart §2).

### Tests for User Story 3

- [x] T044 [P] [US3] Add export correctness tests in `backend/GymNotebook.Tests/ExportTests.cs`:
  - Field-by-field comparison against a seeded account A with canary account B, covering Unicode, decimals, local dates, nulls, ordering and foreign keys.
  - An unused exercise and repeated blocks are present.
  - An empty account exports empty arrays and a null acknowledgement.
  - There are zero credentials, `TokenVersion` or `SignInSuspendedAt` values and zero B values.
  - The headers match contracts/api.md.
- [x] T045 [P] [US3] Add export authorization tests in `backend/GymNotebook.Tests/ExportAuthTests.cs`:
  - A wrong password gets 400 `password_verification_failed` with no file.
  - An expired or revoked token gets 401.
  - More than 10 attempts in 60 s gets 429.
  - A second concurrent export for the same account gets 429 through the export-namespace lock (P12), including from a second host.
  - Non-JSON gets 415.
  - With the flag off, the route returns 404.
- [x] T046 [P] [US3] Add export consistency and cancellation tests in `backend/GymNotebook.Tests/ExportCoordinationTests.cs`:
  - A barrier between table queries while another connection edits a workout still yields one pre-change snapshot.
  - Deletion, token expiry or a password change mid-stream aborts delivery within one chunk.
  - No persistent export copy remains after an abort.
  - The 120 s cap aborts the stream.
- [x] T047 [P] [US3] Add a performance test with the reference fixture (1,000 workouts × 10 blocks × 10 sets = 100,000 sets) in `backend/GymNotebook.Tests/ExportPerformanceTests.cs`. Record duration, payload size and peak memory, targeting ≤ 60 s. This is a regression check; the SC-003 evidence comes from the deployed run (analysis U1).
- [x] T048 [P] [US3] Add Vitest tests for the download helper (object URL created only for a complete response and revoked on finish/cancel; abort handling) in `frontend/src/api/download.test.ts`.

### Implementation for User Story 3

- [x] T049 [US3] Implement the export in `backend/GymNotebook.Api/NotebookExport.cs`, with the field guide from contracts/api.md (depends on T019):
  - A short initialization guard on a separate READ COMMITTED connection verifies the password and token and establishes the snapshot.
  - One read-only REPEATABLE READ snapshot transaction captures `snapshotAt` from the database clock and takes `pg_try_advisory_xact_lock(<export namespace>, userId)`, returning 429 if it is held.
  - Enumerate with keyset batches of 1,000 rows, streamed through `Utf8JsonWriter`.
  - Before each chunk, run a delivery guard on a separate short READ COMMITTED transaction, with a 10 s write timeout.
  - Enforce the 120 s cap.
  - Write the closing JSON bytes only after the final authorization check.
  - Dispose of the snapshot on any abort.
- [x] T050 [US3] Add a per-account sensitive-operation rate-limit policy (10 attempts per 60 s, no queue, partitioned by validated user ID, in process) in `backend/GymNotebook.Api/Program.cs`. Comment the documented per-instance bound (P12).
- [x] T051 [US3] Map `POST /account/export` in `backend/GymNotebook.Api/PrivacyEndpoints.cs`:
  - Flag-gated, on the filter allow-list, with the auth and sensitive rate-limit policies.
  - Require JSON, and don't trim passwords.
  - Headers: `Content-Disposition: attachment; filename="gym-notebook-export.json"` and `Cache-Control: no-store` (depends on T049, T050).
- [x] T052 [US3] Add `exportNotebook` with `AbortSignal` support and a download helper in `frontend/src/api/privacy.ts` and `frontend/src/api/download.ts` (makes T048 pass).
- [ ] T053 [US3] (operator) **Release gate.** Run spike Part B if approved in T009. Deploy a disposable Container Apps environment from `infra/`, run the curl matrix (HTTP/1.1 vs HTTP/2, fast vs `--limit-rate`, one vs two replicas), measure the real round-trip time, then tear it down. Record the results and the pass/fail decision rule outcome in `specs/001-privacy-account-lifecycle/research.md`. If it fails, revise R4 before release.
- [x] T054 [US3] Create the export screen `/account/export` in `frontend/src/screens/ExportData.tsx`, with its route in `frontend/src/routes.tsx`:
  - States: idle, verifying, receiving (indeterminate progress), complete, and recoverable failure.
  - Clear the password after submission.
  - Offer retry on failure (depends on T052).
- [x] T055 [US3] Align `docs/ui/README.md` and `docs/ui/prototype.html` with the export screen and states, and record US3 in `PLAN.md`.

**Checkpoint**: With the flag on locally, quickstart §2 passes and T044–T048 pass.

**Done 2026-09-25** on `feat/001-us3-export-api` and `feat/001-us3-export-ui`, with T053 still open:
- T044–T048 pass, and the full backend (166) and frontend suites pass.
- **T047:** the reference export first took about 60 s locally. With stale table statistics, the sets query's join was planned from the workouts and probed every block on every batch. Sets are now paged through their blocks' ids, and the export takes about 0.4 s (9.7 MiB).
- **Contract addition:** a concurrent export gets 429 `{ "code": "export_in_progress" }` (contracts/api.md).
- The owner still has to record the browser walkthrough of quickstart §2.

---

## Phase 6: User Story 4 — Delete my account and leave (Priority: P1)

**Goal**: Password- and confirmation-verified permanent deletion that removes all active data, revokes every session and cancels overlapping work.

**Independent Test**: Delete an account with data and multiple sessions; verify loss of access and removal of active data while the control account is unchanged; exercise cancellation, wrong passwords, failure and retry (quickstart §3–§4).

### Tests for User Story 4

- [x] T056 [P] [US4] Add deletion outcome tests in `backend/GymNotebook.Tests/DeletionTests.cs`:
  - A false or missing confirmation, a wrong password or cancellation leaves the database unchanged.
  - Success removes all of A's account, exercise, workout, block, set and acknowledgement rows while B is value-equivalent to its baseline.
  - Every old token of A gets 401 on every protected route.
  - A lost-response retry gets 401.
  - The response body matches contracts/api.md → Deletion response.
  - With a controlled clock (inject the built-in `TimeProvider`, no new dependency), `backupsExpireBy` is no later than boundary + 30 calendar days and `deletionEvidenceExpiresBy` no later than boundary + 31 days, including month-end boundaries (SC-006).
  - With the flag off, the route returns 404.
- [x] T057 [P] [US4] Add deletion log tests in `backend/GymNotebook.Tests/DeletionLogTests.cs`, using a captured logger:
  - `deletion.intent`, then `deletion.committed` on success.
  - `deletion.rolled_back` on an injected pre-commit failure.
  - The lines contain only the event, `PrivacyAccountId` and `DeletionBoundaryAt`, with no username, ID, token or content.
  - An ambiguous commit gets 503 `deletion_outcome_unknown`.
- [x] T058 [P] [US4] Add deletion concurrency tests in `backend/GymNotebook.Tests/DeletionConcurrencyTests.cs`, using the two-host fixture and both orderings, per the quickstart §4 table:
  - Workout/set/exercise writes, bulk replace and exercise merge.
  - Password change.
  - Acknowledgement and `/auth/me`.
  - Export and ordinary reads.
  - A slow client.
  - An exclusive wait over 15 s gets 503.
  - Re-registering the username gets a new UUID and inherits nothing.
- [x] T059 [P] [US4] Add a performance test deleting the 100,000-set reference account within 60 s, recording wait and commit durations separately, in `backend/GymNotebook.Tests/DeletionPerformanceTests.cs`. This is a regression check; the SC-005 evidence comes from the deployed run (analysis U1).
- [ ] T060 [P] [US4] Add Vitest tests for invalidation handling in `frontend/src/auth/invalidation.test.ts`:
  - It clears the token and app-owned state and aborts pending requests.
  - It notifies same-origin tabs.
  - Back/forward-cache restores revalidate.

### Implementation for User Story 4

- [x] T061 [US4] Implement account deletion in `backend/GymNotebook.Api/AccountLifecycle.cs` (implemented in its own `AccountDeletion.cs`, so the Q2d scan's allow-list names exactly the deletion code). It owns its transaction through the T022 exclusive-guard helper and is not wrapped by the shared filter (analysis I1) (depends on T019, T022):
  - Take exclusive access with a 15 s wait and a 30 s statement timeout, then freshly check the password and confirmation.
  - Capture the boundary, then log `deletion.intent`.
  - In one transaction, delete workouts (with cascading blocks and sets), then exercises, then User.
  - Commit, then log `deletion.committed`; log `deletion.rolled_back` on definitive rollback.
  - Map an uncertain commit to 503 `deletion_outcome_unknown`.
- [x] T062 [US4] Map `POST /account/delete` in `backend/GymNotebook.Api/PrivacyEndpoints.cs`:
  - Flag-gated, with the auth and sensitive rate-limit policies.
  - Require JSON with `confirmDeletion: true`.
  - Return the minimal outcome body with `retentionBoundaryAt`, `backupsExpireBy`, `deletionEvidenceExpiresBy` and `logRetentionNotice`, and no token (depends on T061).
- [ ] T063 [US4] Implement global invalidation handling in `frontend/src/auth/invalidation.ts` and wire it into `frontend/src/api/client.ts` (makes T060 pass):
  - On an observed 401, clear the token, notebook/draft state, pending fetches and export object URLs.
  - Notify other tabs through the existing storage key/event.
  - Revalidate on `pageshow` restores.
  - A 400 `password_verification_failed` stays local to the form.
- [ ] T064 [P] [US4] Add `deleteAccount` and the deletion outcome types in `frontend/src/api/privacy.ts`.
- [ ] T065 [US4] Create the deletion screen `/account/delete` in `frontend/src/screens/DeleteAccount.tsx` (depends on T064):
  - Show the consequences, the optional export link, the backup deadline, the deletion-evidence expiry and the restricted log exception.
  - Keep a separate password and confirmation step.
  - Disable resubmission while pending.
  - Handle outcomes: 503 retry copy, uncertain-outcome contact copy, and a 401 that never certifies deletion.
- [ ] T066 [US4] Create the completion screen `/account/deleted` in `frontend/src/screens/AccountDeleted.tsx`. It renders only the actual success state; direct navigation shows a neutral signed-out state. Register both routes in `frontend/src/routes.tsx`.
- [ ] T067 [US4] Add the Q5 duplicate-retry warning copy for the re-sign-in state after a 401 on a write, in `frontend/src/api/authErrors.ts` or the affected screen, and document it in `docs/ui/README.md`.
- [ ] T068 [US4] Align `docs/ui/README.md` and `docs/ui/prototype.html` with the deletion flow and states, and record US4 in `PLAN.md`.

**Checkpoint**: With the flag on locally, quickstart §3–§4 pass and T056–T060 pass.

---

## Phase 7: User Story 5 — Keep retention and suppliers accountable (Priority: P1)

**Goal**: A verifiable retention schedule, supplier inventory and restore runbook, proven by an isolated restore exercise.

**Independent Test**: Review the inventory and retention schedule; restore a pre-deletion point into an isolated environment and verify deleted records are removed before access is allowed (quickstart §5).

- [ ] T069 [P] [US5] Pin Log Analytics tables to 30 days (the App\*, `Usage` and `AzureActivity` tables, P26) and evaluate `immediatePurgeDataOn30Days` in `infra/modules/log-analytics.bicep`. Verify the live result after deployment.
- [ ] T070 [P] [US5] (operator) Write `docs/privacy/suppliers.md` (FR-023):
  - Entries for Azure (Container Apps and Log Analytics, swedencentral) and Neon (aws-eu-central-1), with role, purpose, categories, locations, agreement/transfer evidence and deletion assistance.
  - Discovery results for DNS/CDN and support recipients.
  - Remove Google Fonts once T001 is verified.
- [ ] T071 [P] [US5] (operator) Write `docs/privacy/retention.md` (FR-018), per contracts/operations.md → Retention schedule contract. Include the deletion log lines, the preserved pre-restore branch and the Neon 6-hour history window.
- [ ] T072 [P] [US5] (operator) Write the restore runbook `docs/privacy/restore.md`, per contracts/operations.md → Restore contract:
  - Isolate by disabling API ingress, then restore with `--preserve-under-name`.
  - Diff by UUID and re-delete.
  - Log fallback with an ingestion-gap check; manual SQL for `SignInSuspendedAt` and its resolution.
  - Copy credentials from the preserved branch.
  - Rotate the JWT secret directly into the Container Apps secret.
  - Delete the preserved branch, then reopen.
  - Neon drift re-check trigger.
- [ ] T073 [P] [US5] (operator) Write `docs/privacy/rights-requests.md` (FR-025): monitored contact, proportionate verification, calendar-month deadlines, and the register's own retention period and location (Q8).
- [ ] T074 [US5] (operator) **Release gate.** Run the isolated restore exercise in a separate Neon test project, per quickstart §5 steps 4–5 (depends on T061, T072):
  - Primary diff re-deletes A while B stays intact.
  - B's password change after `T` survives through the credential copy.
  - A token from an account registered after `T` fails after rotation.
  - Fallback cases: logs-only reconciliation, a log gap keeps access closed, intent-only leads to suspension, intent plus rolled-back is untouched, replay twice, and the pre-UUID target.

  Record the evidence in `docs/privacy/release-checklist.md`.
- [ ] T075 [US5] (operator) **Release gate.** Scan the stored Container Apps console log content for IPs, usernames, tokens or connection strings (the item left open in Q1). Confirm that no nginx access lines newer than T002's deployment exist and that older ones have aged out; if the flag must be enabled before then, purge them instead. Verify the client address the per-IP limiter sees in production (research R10 finding). Record the results in `specs/001-privacy-account-lifecycle/research.md` → R7/R10, and open a separate fix if the limiter sees only the ingress address.
- [ ] T076 [US5] (operator) **Release gate.** Verify provider settings and agreements against the notice, retention schedule and inventory (FR-024), including Neon's internal durability copies. Verify that only the operator can create branches or restore in the Neon project (console members and API keys), so history stays restricted to recovery use (FR-022). Mark anything unknown as unverified in `docs/privacy/suppliers.md`.
- [ ] T077 [US5] (operator) **Release gate.** Record observed disposal evidence for SC-006 in `docs/privacy/release-checklist.md`:
  - Query the oldest row age per table in `log-gymnote-prod-58dd` and confirm it never exceeds the 30-day limit, allowing for the purge lag noted in research R7.
  - Confirm that Neon refuses a branch or restore at a point older than the 6-hour history window.
  - Record that no persistent export copies exist, since none are created by design (FR-013).

**Checkpoint**: The restore exercise passes with zero deleted-account records after reopening (SC-006), and every supplier and retention entry has evidence or an explicit unverified status.

---

## Phase 7a: User Story 6 — Choose whether to record optional workout details (Priority: P1)

**Goal**: Explicit, withdrawable consent for workout title, location, notes and bodyweight (spec amendment 2026-09-25, FR-029–FR-035). Withdrawal clears those details and keeps the rest of the notebook.

**Independent Test**: Walk through grant, refusal, withdrawal, re-grant, rejected writes and the transition question with a fresh, a consenting and an existing account (quickstart §6a).

**Proposals approved** 2026-09-25 (plan.md Q10, P29–P35). Depends on Phase 2 and US1 (the notebook gate). Independent of US3 and US4, except where T096 notes otherwise.

### Tests for User Story 6

- [ ] T085 [P] [US6] Add consent API tests in `backend/GymNotebook.Tests/OptionalDetailsConsentTests.cs`:
  - `GET /privacy/optional-details-statement` is public, and the three routes return 404 with the flag off.
  - `PUT` accepts only the current statement version (409 `consent_statement_changed` otherwise) and is idempotent, keeping the timestamp.
  - `GET /account/privacy` reports `consent` and `transitionPending` correctly: no details; details without consent; consent.
  - `DELETE` clears the pair and all four fields on every workout of the caller only (canary account untouched), leaves every other field and row unchanged, and returns `clearedWorkouts: 0` on repeat.
- [ ] T086 [P] [US6] Add enforcement tests in `backend/GymNotebook.Tests/OptionalDetailsEnforcementTests.cs`:
  - With the flag on and no consent, `POST /workouts` and `PATCH /workouts/{id}` carrying any non-empty optional detail get 403 `optional_details_consent_required` and store nothing, including the request's other fields.
  - Null, empty or omitted details are accepted. Exercise names are never affected.
  - With consent, all four fields are accepted as today.
  - With the flag off, everything is accepted as today.
- [ ] T087 [P] [US6] Add a withdrawal coordination test in `backend/GymNotebook.Tests/OptionalDetailsCoordinationTests.cs`: a workout save racing withdrawal ends either rejected or cleared, never with a detail stored after withdrawal commits. Use the existing barrier/`pg_locks` pattern, no sleeps.
- [ ] T088 [P] [US6] Add Vitest tests for the gate decision (notice first, then the transition question only when `transitionPending`) and for dropping optional details from a draft, in `frontend/src/auth/noticeGate.test.ts` and `frontend/src/screens/newWorkoutDraft.test.ts`.

### Implementation for User Story 6

- [ ] T089 [US6] (owner) Write the consent statement content in `docs/privacy/consent/` (index plus one version), per FR-030. Real wording, not synthetic: it is shown to users with the notice's reviewed content (T043).
- [ ] T090 [US6] Add `OptionalDetailsConsentVersion` and `OptionalDetailsConsentedAt` to `backend/GymNotebook.Api/User.cs` with a both-or-neither check constraint, and generate and review the migration. No backfill (P29).
- [ ] T091 [US6] Embed and validate the consent statement at startup, reusing the `PrivacyNoticeCatalog` pattern, in `backend/GymNotebook.Api/OptionalDetailsConsentCatalog.cs` and `GymNotebook.Api.csproj` (depends on T089; a synthetic test version can stand in until then).
- [ ] T092 [US6] Map the three routes and extend `GET /account/privacy` in `backend/GymNotebook.Api/PrivacyEndpoints.cs`: flag-gated, lifecycle filter, `Cache-Control: no-store`. Withdrawal is one transaction with a single `ExecuteUpdateAsync` (makes T085 pass).
- [ ] T093 [US6] Enforce consent in `POST /workouts` and `PATCH /workouts/{id}` in `backend/GymNotebook.Api/Program.cs`, only when the flag is on, before any change. Comment the owner-accepted interim behaviour with the flag off (makes T086–T087 pass).
- [ ] T094 [P] [US6] Add the statement, grant and withdraw calls and the new account-state fields in `frontend/src/api/privacy.ts`, and map the 403 code in `frontend/src/api/client.ts`.
- [ ] T095 [US6] Create the consent screen `/account/privacy/optional-details` in `frontend/src/screens/OptionalDetailsConsent.tsx`, including the withdrawal review step. Hide the four inputs and show the opt-in entry in `NewWorkout.tsx` and `WorkoutDetail.tsx`. Add the transition question to the notebook gate in `NoticeGate.tsx`/`requireNoticeAcknowledged.ts`. Link from `AccountPrivacy.tsx` (depends on T094; makes T088 pass).
- [ ] T096 [US6] Include `privacyRecords.optionalDetailsConsent` in the export: in T049 if US3 is not yet merged, otherwise here with a T044 test update. Account deletion removes the pair with User, so no US4 change is needed.
- [ ] T097 [US6] (operator) Write the transition clearing SQL and its follow-up zero-count query in `docs/privacy/retention.md`, and add a dated step to `docs/privacy/release-checklist.md` for running it 30 days after T084.
- [ ] T098 [US6] Align `docs/ui/README.md` and `docs/ui/prototype.html` with the editor opt-in, the consent screen and the transition question, and record US6 in `PLAN.md` and the notice's information section (FR-002: optional information and the consequence of not giving it).

**Checkpoint**: With the flag on locally, quickstart §6a passes and T085–T088 pass. With the flag off, workout writes behave as today.

---

## Phase 8: Polish & Release

**Purpose**: Cross-cutting acceptance evidence, documentation alignment and the controlled production switch.

- [ ] T078 [P] Create `docs/privacy/release-checklist.md` with owner, date, evidence reference and status for: a line confirming no exceptional retention beyond the FR-019/FR-020 limits was discovered, or the reviewed amendment if one was (FR-021); and SC-001–SC-008 and each release gate in plan.md.
- [ ] T079 [P] Update `README.md` with how to run the new privacy tests, the export performance fixture and the local flag setup. Update `PLAN.md` with the milestone log entry for this feature.
- [ ] T080 Run all required checks: `dotnet format backend/GymNotebook.sln --verify-no-changes` and `dotnet test backend/GymNotebook.sln`, then `npm run typecheck`, `npm run lint`, `npm run format:check`, `npm test` and `npm run build` in `frontend/`.
- [ ] T081 (operator) **Release gate.** Run the reference export and deletion once in a disposable deployed environment with the real Neon cross-region path, using the T053 Part B environment if approved (analysis U1):
  - Record client connection, regions, measured round-trip time, duration, payload size and peak memory.
  - This run, not the local tests, is the SC-003 and SC-005 evidence; record it in `docs/privacy/release-checklist.md`.
  - Tear the environment down afterwards.
- [ ] T082 (owner) **Release gate.** Complete and record the owner walkthrough of the notice, export, deletion and optional-details consent (grant and withdraw) on mobile and keyboard-only desktop, with each flow under three minutes excluding download (SC-004), in `docs/privacy/release-checklist.md`.
- [ ] T083 (owner) **Release gate.** Complete the rights-request practice cases within the calendar-month deadline (SC-007), and record them in `docs/privacy/release-checklist.md`.
- [ ] T084 (owner) Once every release gate in plan.md and `docs/privacy/release-checklist.md` has evidence, change `PRIVACY_LIFECYCLE_ENABLED` to `true` in `infra/modules/container-app-api.bicep` through a reviewed PR, and verify the deployed feature with the flag on.

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (Phase 1)**: No dependencies. T001 and T002 are independent of everything, each its own PR from `main`; land T002 first, because its 31-day age-out gates T075.
- **Foundational (Phase 2)**: Depends on T003–T006. T008 (spike Part A) blocks T019, and T019 blocks the endpoint work in US1, US3 and US4.
- **US1 (Phase 3)**: Depends on Phase 2.
- **US2 (Phase 4)**: No code dependency; owner work can start immediately and run alongside everything. Its gates (T042, T043) block release.
- **US3 (Phase 5)**: Depends on Phase 2. T053 depends on T009 and on a deployable export.
- **US4 (Phase 6)**: Depends on Phase 2. The optional export link in T065 is soft-linked to US3; deletion works without it.
- **US6 (Phase 7a)**: Depends on Phase 2 and US1; its proposals were approved as Q10. T089 needs T043's reviewed notice. T096 is coordinated with US3.
- **US5 (Phase 7)**: T069–T073 can start any time. T074 depends on US4 (T061) and T072.
- **Polish (Phase 8)**: T081–T084 depend on all stories and release gates; T081 also uses the T053 environment if Part B was approved.

### Within Each Story

- Tests are written first and fail before implementation.
- Schema, then backend helpers, then endpoints, then the frontend API, then screens, then documentation alignment.
- Each story's documentation update (PLAN.md/docs/ui) lands in the same PR as its behavior (Principle VI).
- Every new screen task (T034–T036, T054, T065, T066, T095) includes the accessibility rules in contracts/ui.md → Invalidation and accessibility (FR-001):
  - labelled password inputs, visible focus and a logical tab order;
  - accessible status and error announcements;
  - focus restoration after navigation or failure;
  - no focus traps, and no meaning carried by color alone.

  T082 (the walkthrough) verifies them; it does not replace building them.

### Suggested PR Boundaries (per plan.md Phase 1)

1. T001 fonts and T002 nginx access log (each from `main`)
2. T003–T007 flag
3. T008 spike (not merged)
4. T010–T027 lifecycle foundation
5. US1
6. US3
7. US4
8. US5 infrastructure and documents
8a. US6 consent (T085–T098)
9. Release evidence and the flag switch

---

## Parallel Examples

```text
# Phase 2 tests, all in different files:
T011 LifecycleCoverageTests.cs   T012 LifecycleCoordinationTests.cs   T013 LifecycleWriteDeliveryTests.cs
T014 LoginRaceTests.cs           T015 SuspensionTests.cs               T016 AccountIdentityTests.cs

# US3 tests:
T044 ExportTests.cs   T045 ExportAuthTests.cs   T046 ExportCoordinationTests.cs   T047 ExportPerformanceTests.cs   T048 download.test.ts

# Owner/operator work alongside the code at any time:
T041 processing-decision.md   T070 suppliers.md   T071 retention.md   T072 restore.md   T073 rights-requests.md
```

---

## Implementation Strategy

### MVP First (US1)

1. Complete Phase 1 (flag off by default).
2. Complete Phase 2, including the spike.
3. Complete US1, then validate quickstart §1 locally with the flag on.
4. Merge with the flag off in production. Nothing user-visible changes, and the filter's overhead becomes measurable in production.

### Incremental Delivery

US1 → US3 → US4 → US6 → US5, each merged with the flag off and validated locally with it on. US2 and the operator documents proceed in parallel. The flag flips only in T084.

---

## Open Items Carried Into Implementation (Principle VII)

These are shown as unresolved rather than decided by generation:

- **T031:** how the notice artifacts reach the API image. **Resolved 2026-09-25:** embedded resources, with the repository root as the backend build context (owner decision).
- **T026:** how the Q2d invariant is tested. A source scan is suggested; the owner may prefer another guard.
