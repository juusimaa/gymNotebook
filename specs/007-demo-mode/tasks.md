# Tasks: Demo Mode

**Input**: [plan.md](plan.md), [spec.md](spec.md), [research.md](research.md), [data-model.md](data-model.md), [contracts/api.md](contracts/api.md), [quickstart.md](quickstart.md)

**Status**: Draft for owner review (Principle VII). AI implements by default; the owner names any task they will write themselves.

**Tests**: Principle IV requires them. Backend tests use xUnit + Testcontainers (real Postgres 18), follow Arrange/Act/Assert and are named `MethodName_Scenario_ExpectedResult`. Existing suites must pass **unchanged**.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: can run in parallel (different files, no dependency on an incomplete task)
- **[Story]**: US1 Try the demo, US2 Reset, US3 Cleanup, US4 Stays a demo (spec.md)
- **PR 1** = backend + `PLAN.md`/`README.md` (T001–T063). **PR 2** = screens + `docs/ui/` (T070 onward).

Paths: backend `backend/GymNotebook.Api/`, tests `backend/GymNotebook.Tests/`, frontend `frontend/src/`.

---

## Phase 1: Setup

- [x] T001 Spec, plan, research, data model, contract, quickstart and tasks in `specs/007-demo-mode/`; constitution 3.2.0 (Principle V demo exception) in `.specify/memory/constitution.md`.

---

## Phase 2: Foundational (PR 1)

**Purpose**: the column, the settings and the restore split that every story uses.

- [x] T010 Add `public DateTimeOffset? DemoExpiresAt { get; set; }` to `backend/GymNotebook.Api/User.cs`, with a comment: null = registered account; set = demo account valid until then, never extended (data-model.md).
- [x] T011 In `backend/GymNotebook.Api/Data/AppDbContext.cs`, add the partial index `ix_users_demo_expires_at` on `DemoExpiresAt` with filter `demo_expires_at IS NOT NULL`.
- [x] T012 Generate migration `AddDemoAccounts` (`dotnet ef migrations add AddDemoAccounts --project backend/GymNotebook.Api`). Review it: one nullable `timestamptz` column, one partial index, nothing else.
- [x] T013 [P] Create `backend/GymNotebook.Api/DemoOptions.cs`:
  - settings with defaults: `LifetimeHours` = 24, `MaxLiveAccounts` = 200, `CleanupIntervalMinutes` = 15;
  - `Validate()`: every value must be positive, and `CleanupIntervalMinutes` at most 1440 (`PeriodicTimer` refuses periods over ~49 days, which would stop the host at boot; found during implementation);
  - `public const string DisplayName = "Demo Lifter"`;
  - `public const int MaxUtcOffsetMinutes = 840`.

  Bind it from `Demo` in `backend/GymNotebook.Api/Program.cs` and register it as a singleton. Read `DEMO_MODE_ENABLED` with the exact-`"true"` rule and a comment like the privacy flag's. Read the `DemoRateLimit:PermitLimit` (3) and `DemoRateLimit:WindowSeconds` (3600) settings.
- [x] T014 Split `NotebookRestore.RestoreAsync` (R5) in `backend/GymNotebook.Api/NotebookRestore.cs`:
  - new `public async Task<RestoreResponse> ImportAsync(RestoreFile file, int userId, bool privacyLifecycleEnabled, CancellationToken ct)`: everything from the started/prepared split to the final save. It requires `db.Database.CurrentTransaction` and throws otherwise.
  - `RestoreAsync` keeps the restore lock, the `statement_timeout` and `Results.Ok(...)`.

  No behaviour change: `RestoreTests` passes unchanged.
- [x] T015 [P] Add `public const string DemoAction = "demo";` to `backend/GymNotebook.Api/Turnstile.cs`, with its comment in the action list.
- [x] T016 Create `backend/GymNotebook.Tests/DemoEnabledGymNotebookFactory.cs`. It extends `GymNotebookFactory`, sets `DEMO_MODE_ENABLED=true` and a generous `DemoRateLimit:PermitLimit` (1000), and keeps `Demo:CleanupIntervalMinutes` large, so the worker doesn't run during tests unless asked.
- [x] T017 Add a test helper `DemoTestSupport.StartDemoAsync(HttpClient, int offset = 180)` in `backend/GymNotebook.Tests/DemoTestSupport.cs`. It returns the token and sets the bearer header.

**Checkpoint**: builds, migration applied by the fixtures, all existing tests green.

---

## Phase 3: User Story 1 — Try the app with a full notebook (P1, PR 1) 🎯 MVP

**Goal**: `POST /auth/demo` creates, seeds and signs in a demo account.

**Independent test**: start a demo; Sessions, a page, Templates and both kinds of Progress show realistic data.

### Tests

- [x] T020 [P] [US1] `backend/GymNotebook.Tests/DemoSeedTests.cs` (pure, no database). Each test runs over seeds 1–50 with `localNow` fixed (`2026-10-16T18:30+03:00`):
  - `Build_AnySeed_PassesRestoreValidation` (`NotebookRestore.Validate` returns null);
  - `Build_AnySeed_HasAboutTwelveWeeksOfFinishedPagesEndingBeforeToday` (30–40 finished pages, the first ≥ 80 days back, the last within 4 days, every `StartedAt` < `localNow`);
  - `Build_AnySeed_HasOneInProgressPageTodayWithDoneAndPlannedSets`;
  - `Build_AnySeed_HasThreeTemplatesWithSets`;
  - `Build_AnySeed_IncludesWarmupsSkippedSetsAndBodyweightExercises` (Pull-up and Dips are `IsBodyweight` with null weights);
  - `Build_AnySeed_WeightsAreWholePlateSteps` (every weight is a multiple of 2.5 between 20 and 300);
  - `Build_AnySeed_MainLiftTrendsUpWithOneDeloadWeek` (Back Squat's best working weight in the last week is above the first week's, and exactly one week is lower than both neighbours);
  - `Build_AnySeed_SomePagesCarryOptionalDetails`;
  - `Build_SameSeed_IsDeterministic`;
  - `Build_LocalNowEarlyMorning_StillPutsEveryPageInThePast`.
- [x] T021 [P] [US1] `backend/GymNotebook.Tests/DemoStartTests.cs` with `DemoEnabledGymNotebookFactory`:
  - `StartDemo_Valid_ReturnsTokenThatReadsSeededNotebook` (`/auth/me` `demoExpiresAt` ≈ now + 24 h; `/workouts` and `/templates` non-empty; `/exercises/{id}/history` for Back Squat has ≥ 15 points — one point per session, and squats are on the Lower days of the Upper/Lower rotation, about half of the ~36; corrected from ≥ 30 during implementation);
  - `StartDemo_TwoStarts_GetSeparateAccounts` (each sees only its own data; the other's workout id → 404);
  - `StartDemo_OffsetOutOfRange_Returns400` (841, −841, missing);
  - `StartDemo_AtLiveCap_Returns503AndCreatesNothing` (a factory variant with `Demo:MaxLiveAccounts=1`);
  - `StartDemo_ExpiredDemosDoNotCountTowardCap`;
  - `GetMe_RegisteredAccount_HasNullDemoExpiresAt`.
- [x] T022 [P] [US1] Flag-off, captcha and rate-limit tests in `backend/GymNotebook.Tests/DemoStartTests.cs`:
  - `StartDemo_FlagOff_Returns404` (plain `GymNotebookFactory`);
  - `StartDemo_FlagCapitalized_Returns404` (`"True"`);
  - `StartDemo_TurnstileRejects_Returns400CaptchaAndCreatesNothing` (reuse `TurnstileGymNotebookFactory`'s stub pattern with the flag on);
  - `StartDemo_OverPerIpLimit_Returns429` (a factory variant with `DemoRateLimit:PermitLimit=2`).
- [x] T023 [P] [US1] `StartDemo_PrivacyOnWithoutConsent_SeedsNoOptionalDetailsAndRequiresNotice` in `backend/GymNotebook.Tests/DemoStartTests.cs` (privacy-enabled + demo factory): no page has a title, location, notes or bodyweight; `/account/privacy` says `requiresAcknowledgement`.

### Implementation

- [x] T024 [US1] Create `backend/GymNotebook.Api/DemoSeed.cs`: `public static RestoreFile Build(DateTimeOffset localNow, Random random)`, per research R4. Constants (exercise list, start weights, weekly steps, deload week, rep schemes, notes, locations) sit at the top of the class with comments explaining each training choice. Ids are sequential per table. `FormatVersion = 2`.
- [x] T025 [US1] Create the request records `backend/GymNotebook.Api/DemoStartRequest.cs` (`string? TurnstileToken, int? UtcOffsetMinutes`) and `backend/GymNotebook.Api/DemoResetRequest.cs` (`int? UtcOffsetMinutes`).
- [x] T026 [US1] Create `backend/GymNotebook.Api/DemoEndpoints.cs` with `MapDemoEndpoints(this IEndpointRouteBuilder app, ...)` and the `POST /auth/demo` handler, per contracts/api.md and research R3. Steps:
  1. validate the offset;
  2. Turnstile with `DemoAction`;
  3. begin a transaction and take `pg_advisory_xact_lock(DemoLockNamespace, 0)` (namespace `0x44454D4F`, "DEMO");
  4. count `DemoExpiresAt > now` and answer 503 `demo_unavailable` with `Retry-After: 300` at the cap;
  5. insert the `User` with the fields from data-model.md (email `"demo:" + Guid.NewGuid()`);
  6. save, then `DemoSeed.Build(serverUtcNow.ToOffset(offset), new Random())`;
  7. `Validate` (throw `InvalidOperationException` if not null);
  8. `ImportAsync`, commit, then `JwtTokenFactory.CreateToken`.

  Add the OpenAPI metadata, `.RequireRateLimiting("demo")` and the `NoStore` filter.
- [x] T027 [US1] In `backend/GymNotebook.Api/Program.cs`:
  - add the `"demo"` per-IP rate-limit policy;
  - call `app.MapDemoEndpoints(...)` only when the flag is on;
  - extend `MeResponse` (`backend/GymNotebook.Api/MeResponse.cs`) with `DateTimeOffset? DemoExpiresAt`, and project it in `/auth/me`.

**Checkpoint**: US1 tests green; the quickstart steps 3–4 work.

---

## Phase 4: User Story 2 — Reset the demo (P2, PR 1)

**Goal**: `POST /account/demo/reset` replaces the notebook under exclusive access.

**Independent test**: change the demo notebook, reset it, and get a fresh demo back.

### Tests

- [x] T030 [P] [US2] `backend/GymNotebook.Tests/DemoResetTests.cs` (demo factory):
  - `ResetDemo_AfterChanges_ReplacesWholeNotebook` (deleted pages, a renamed exercise and an added template are gone; counts match a fresh demo's range);
  - `ResetDemo_KeepsSessionTokenAndExpiry` (the same token still works; `demoExpiresAt` unchanged);
  - `ResetDemo_RegisteredAccount_Returns403NotADemoAndChangesNothing`;
  - `ResetDemo_OffsetOutOfRange_Returns400`;
  - `ResetDemo_FlagOff_Returns404`;
  - `ResetDemo_OtherDemoUnaffected`.
- [x] T031 [P] [US2] Privacy-enabled + demo factory, in `backend/GymNotebook.Tests/DemoResetTests.cs`:
  - `ResetDemo_AfterConsent_IncludesOptionalDetails`;
  - `ResetDemo_WithoutConsent_OmitsOptionalDetails`;
  - `ResetDemo_KeepsNoticeAcknowledgementAndConsent`.
- [x] T032 [P] [US2] `ResetDemo_WhileAnotherRequestHoldsSharedAccess_WaitsThenCompletes` and `ResetDemo_ExclusiveWaitTimesOut_Returns503AndChangesNothing` in `backend/GymNotebook.Tests/DemoResetTests.cs`. Follow `LifecycleCoordinationTests`' pattern of holding the lock on a second connection.

### Implementation

- [x] T033 [US2] In `backend/GymNotebook.Api/AccountDeletion.cs`, extract `internal static Task DeleteNotebookAsync(AppDbContext db, int userId)`. It runs the three set-based deletes (workouts, templates, exercises, in that order, with `CancellationToken.None`), and `RunAsync` keeps its behaviour by calling it. `DeletionTests` passes unchanged.
- [x] T034 [US2] Add the `POST /account/demo/reset` handler to `backend/GymNotebook.Api/DemoEndpoints.cs` (research R6). Steps:
  1. validate the offset;
  2. take claims;
  3. own the transaction and `AcquireExclusiveAsync` (map the outcome with `ToResult`);
  4. read `DemoExpiresAt` untracked: null → 403 `not_a_demo`, at or before now → 401;
  5. `DeleteNotebookAsync`, seed, `Validate`, `ImportAsync`, commit, 204.

  Use `.RequireAuthorization()`, `.RequireRateLimiting("restore")` and `NoStore`. A comment says why it isn't under the shared filter.
- [x] T035 [US2] Add `["POST /account/demo/reset"]` with its reason to the allow-list in `backend/GymNotebook.Tests/LifecycleCoverageTests.cs`. The test's host must have the demo flag on, so the route is enumerated: extend the privacy-enabled factory it uses, or add the flag to it.

**Checkpoint**: US2 tests green; the quickstart step 5 works.

---

## Phase 5: User Story 3 — Demo accounts clean themselves up (P2, PR 1)

**Goal**: expired demos stop working at once and are deleted by the worker.

**Independent test**: expire a demo by SQL; its token gets 401; after the cleanup runs, the row and its notebook are gone.

### Tests

- [x] T040 [P] [US3] `backend/GymNotebook.Tests/DemoExpiryTests.cs`:
  - `AnyRoute_ExpiredDemoToken_Returns401` (`/auth/me`, `/workouts`, `/auth/token`);
  - `DeleteExpiredDemo_Expired_RemovesAccountAndNotebookAndLogsEvidence` (reuse `DeletionLogTests`' log capture for the intent and committed lines);
  - `DeleteExpiredDemo_NotExpiredOrRegistered_DeletesNothing`;
  - `CleanupRunOnce_DeletesOnlyExpiredDemos` (a mix of an expired demo, a live demo and a registered account).

### Implementation

- [x] T041 [US3] Expiry checks (research R2):
  - In `OnTokenValidated` in `backend/GymNotebook.Api/Program.cs`, fail when `user.DemoExpiresAt is { } expires && expires <= clock.GetUtcNow()`. Resolve the clock from `RequestServices`.
  - In `backend/GymNotebook.Api/AccountLifecycle.cs`, extend the `requireConfirmedEmail` check statement with `AND (demo_expires_at IS NULL OR demo_expires_at > now())`, and update its comment.
- [x] T042 [US3] Add `public async Task<bool> DeleteExpiredDemoAsync(int userId, CancellationToken ct)` to `backend/GymNotebook.Api/AccountDeletion.cs`:
  - read the token version and the privacy id untracked, and return false if the row is gone or isn't an expired demo;
  - own a transaction and `AcquireExclusiveAsync`;
  - re-check, under the lock, that the row is an expired demo;
  - run the same intent, `DeleteNotebookAsync`, user-row delete, commit and committed/rolled-back evidence lines as `RunAsync`, sharing private helpers rather than copying them.

  The `AccountIdentityTests` source scan must still pass (still the only file that deletes users).
- [x] T043 [US3] Create `backend/GymNotebook.Api/DemoCleanupWorker.cs`: a `BackgroundService` built with `IServiceScopeFactory`, `DemoOptions`, `TimeProvider` and a logger.
  - Expose `public async Task<int> RunOnceAsync(CancellationToken ct)`: select up to 50 ids where `DemoExpiresAt <= now`; for each, call `DeleteExpiredDemoAsync` in its own scope; catch only database failures (`AccountLifecycle.IsDatabaseFailure`) and log them without the exception message.
  - `ExecuteAsync` loops with `PeriodicTimer(CleanupIntervalMinutes)`.
  - Register it as a hosted service in `backend/GymNotebook.Api/Program.cs`, whatever the flag says (a comment explains why).

**Checkpoint**: US3 tests green.

---

## Phase 6: User Story 4 — Demo accounts can't become real accounts (P3, PR 1)

### Tests

- [x] T050 [P] [US4] `backend/GymNotebook.Tests/DemoRefusalTests.cs`:
  - `Login_DemoAddress_Returns401` (the stored `demo:` address with any password);
  - `ChangePassword_Demo_Returns403DemoAccount`;
  - `Export_Demo_Returns403DemoAccount`;
  - `DeleteAccount_Demo_Returns403DemoAccount` (privacy + demo factory);
  - `Register_DemoAddress_Returns400AndQueuesNoEmail`;
  - `PasswordReset_DemoAddress_Returns400AndQueuesNoEmail` (`MemoryEmailSender` empty).

### Implementation

- [x] T051 [US4] Refusals (research R7):
  - In the login handler in `backend/GymNotebook.Api/Program.cs`, add `user.DemoExpiresAt is not null` to the 401 branch after the password check.
  - In `change-password`, answer 403 `demo_account` after loading the user and before BCrypt.
  - In `backend/GymNotebook.Api/NotebookExport.cs` and `AccountDeletion.RunAsync`, answer 403 `demo_account` before the password check, adding `DemoExpiresAt` to the existing projection rather than adding a query.

**Checkpoint**: all PR 1 tests green.

---

## Phase 7: Polish & docs (PR 1)

- [x] T060 [P] Update `PLAN.md`:
  - Data model (`demo_expires_at`);
  - REST API (the two routes and the `/auth/me` field);
  - Auth (a "Demo accounts" paragraph: R1–R3, R7, R8);
  - Rate limiting (the `demo` policy);
  - Milestones (milestone 17, demo mode: PR 1 done, PR 2 next).
- [x] T061 [P] Update `README.md` → Configuration (`DEMO_MODE_ENABLED`, `Demo:*`, `DemoRateLimit:*`, how to try it locally) and `docker-compose.yml` (`DEMO_MODE_ENABLED: "${DEMO_MODE_ENABLED:-false}"`, next to the privacy flag).
- [x] T062 Run `dotnet format backend/GymNotebook.sln --verify-no-changes` and `dotnet test backend/GymNotebook.sln`; all green, existing assertions unchanged.
- [ ] T063 Run the quickstart's manual steps against `docker compose up --build`, then ask the owner for approval before opening PR 1.

---

## Phase 8: Screens (PR 2, after PR 1 merges)

- [x] T070 Mock the demo UI in `docs/ui/README.md` and `docs/ui/prototype.html` for owner approval:
  - Login: a **Try the demo** button with Turnstile;
  - Cover: a demo badge showing the expiry time, **Reset demo data** (confirm dialog, with the hint that a reset loads sample notes after optional details are allowed) and **Leave demo**;
  - the refusal copy for `demo_account`.
- [ ] T071 `frontend/src/api/demo.ts` (`startDemo`, `resetDemo`) and the `demoExpiresAt` field in `frontend/src/api/auth.ts`; Vitest for any helper.
- [ ] T072 Screens in `frontend/src/screens/Login.tsx` and `frontend/src/screens/Cover.tsx`, with loading, error (captcha, `demo_unavailable`, 429) and success states. The Account screens show the `demo_account` refusal.
- [ ] T073 `npm run typecheck`, `npm run lint`, `npm run format:check`, `npm test`; check the UI in the browser; `PLAN.md` milestone → done.

---

## Dependencies

- Phase 2 blocks everything.
- US1 (T024–T027) blocks US2: the reset needs `DemoSeed` and `DemoEndpoints`.
- US3 and US4 depend only on Phase 2 and T026, so they can go in parallel with US2.
- Phase 7 comes after all of US1–US4. Phase 8 comes after PR 1 merges and T070 is approved.

## Parallel opportunities

- T013, T015 and T016 alongside T014.
- The test files of each story (T020–T023, T030–T032, T040, T050) can be written in parallel.
- US2, US3 and US4 implementation in parallel once US1 is done (they touch different methods; T041 and T051 both edit `Program.cs`, so run those two in sequence).

## Implementation strategy

MVP = Phases 2–3 (start a demo with a full notebook). Then reset (US2), expiry/cleanup (US3) and refusals (US4). PR 1 should not be opened without US3: without cleanup and expiry, demos would pile up in production once the flag is on.
