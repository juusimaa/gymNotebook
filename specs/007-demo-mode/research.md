# Research: Demo Mode

**Feature**: [spec.md](spec.md) | **Plan**: [plan.md](plan.md) | **Date**: 2026-10-16

Each entry records a decision, why it was taken and what else was considered. They are drafts for owner review (Principle VII).

## R1 — A demo account is an ordinary `User` with an expiry

**Decision**: Add one nullable column, `users.demo_expires_at` (`timestamptz`), with a partial index `WHERE demo_expires_at IS NOT NULL`. A non-null value means "demo account, valid until then". A demo row also has:

- `email = "demo:" + Guid` (deliberately **no `@`**, see R7);
- `display_name = "Demo Lifter"`;
- `password_hash` = BCrypt of a random GUID that is thrown away;
- `email_verified_at` = creation time;
- a fresh `privacy_account_id`.

The notice acknowledgement and the consent stay null (spec clarification: normal flows).

**Rationale**: Every screen and every rule (ownership, lifecycle lock, consent, set status) then works unchanged. One column answers both "is this a demo" and "until when", so they can never disagree. The partial index keeps the cleanup and cap queries cheap without indexing every real account.

**Alternatives considered**:
- `is_demo` plus `expires_at` columns: two columns that must agree, for no gain.
- A separate `demo_sessions` table: needs a join on every guard check.
- A browser-only mock (rejected by the owner): a second implementation of the API's rules that drifts.

## R2 — Expiry is enforced where tokens are already checked

**Decision**: An expired demo is treated like a revoked token, in the two places that already check every request:

1. `OnTokenValidated` (Program.cs) fails when `user.DemoExpiresAt <= now`, using the row it already loaded.
2. The shared lifecycle guard's check statement (`AccountLifecycle.AcquireAsync`, `requireConfirmedEmail: true` branch) adds `AND (demo_expires_at IS NULL OR demo_expires_at > now())`.

Renewal (`POST /auth/token`) runs under the shared guard, so an expired demo can't renew. A token renewed shortly before expiry dies with the account at the next request. So FR-013 needs no change to the renewal handler.

**Rationale**: No new code path, and the existing tests for revoked tokens describe the behaviour. The guard uses the database clock, as the other lock-time checks do. `OnTokenValidated` uses the injected `TimeProvider`. A difference of a few seconds between the two only moves the cut-off by that much.

**Alternatives considered**: Capping the JWT's `exp` at the demo expiry. That covers only the token, not a request in flight, and still needs the guard check, so the check alone is enough.

## R3 — Starting a demo: `POST /auth/demo`

**Decision**:
- The route is mapped **only** when `DEMO_MODE_ENABLED` is exactly `"true"`, with the same exact-string rule as `PRIVACY_LIFECYCLE_ENABLED`, so it returns 404 otherwise.
- Body: `{ turnstileToken, utcOffsetMinutes }`. Answer: `200 AuthResponse` (the existing `{ token }`), so the frontend's sign-in handling is reused.
- Order:
  1. validate the input (400 `invalid_request`);
  2. run Turnstile with the new action `"demo"` (400 `captcha`, fail closed);
  3. open a transaction and take `pg_advisory_xact_lock(DemoLockNamespace, 0)`;
  4. count live demos (`demo_expires_at > now()`); at the cap, answer 503 `demo_unavailable` with `Retry-After`;
  5. insert the user and seed the notebook (R4, R5);
  6. commit, then mint the token.
- Rate limit: a new per-IP policy `demo`, by default 3 starts per hour (`DemoRateLimit:*`).
- Settings with defaults: `Demo:LifetimeHours` = 24, `Demo:MaxLiveAccounts` = 200, `Demo:CleanupIntervalMinutes` = 15.

**Rationale**:
- One transaction makes the start "all or nothing" (spec edge case: seeding fails partway).
- The global advisory lock makes the cap exact. It serializes demo starts only, which are rare and rate-limited, and never touches other traffic.
- The offset is the only client input the seed needs (R4). Everything else comes from the server clock, so a visitor's wrong device clock can't produce future-dated pages.
- 503, not 429, for the cap: the visitor did nothing wrong, the service is full.

**Alternatives considered**:
- Counting without a lock: two concurrent starts could each see 199 and both insert. That's harmless at this scale, but the lock is two lines and makes the requirement testable.
- Reusing the `auth` policy (10 per minute): far too generous for creating accounts.

## R4 — Generated data: `DemoSeed`

**Decision**: A static, pure function, `DemoSeed.Build(DateTimeOffset localNow, Random random) → RestoreFile` (format version 2). It does no I/O, so it is unit-tested with fixed seeds. Outline (exact numbers are an implementation detail):

- **Exercises**:
  - weighted: Back Squat, Bench Press, Deadlift, Overhead Press, Barbell Row, Romanian Deadlift, Lat Pulldown, Incline Dumbbell Press;
  - bodyweight: Pull-up, Dips.
- **Rotation**: Upper and Lower days alternating, three sessions a week (for example Mon/Wed/Fri), for 12 weeks back from the latest training day before today, about 36 finished pages. Starts are in the late afternoon local time with ±40 min jitter; sessions last 55–80 min.
- **Progression**:
  - each main lift starts at a plausible working weight and rises by about 2.5 kg a week (5 kg for the deadlift), rounded to 2.5 kg plates;
  - rep schemes are 3×5 or 3×8;
  - noise: about 1 in 6 sessions repeats the previous weight, and about 1 in 8 last sets misses a rep;
  - week 8 is a deload at about 85 % weight, so the e1RM chart dips visibly once;
  - bodyweight exercises progress by reps (Pull-up from about 6 to 10, Dips from about 8 to 14).
- **Warm-ups**: the first barbell lift of each session gets two (an empty 20 kg bar ×10, then about 60 % ×5).
- **Skipped sets**: on 3–4 finished pages, a final accessory set is `skipped`.
- **Optional details**:
  - a title ("Upper", "Lower") on most pages;
  - a location on some ("Home gym", "City gym", both made up);
  - short, plausible notes on about 1 in 5 (for example "Bench moved fast today.");
  - bodyweight around 82 kg drifting slowly.
- **Templates**: three — "Upper", "Lower" and "Arms & Core" (one extra, so the template list isn't a mirror of the rotation).
- **Today's page**: started from "Lower" about 40 minutes before `localNow`, `endedAt` null. The first exercise is done, the rest are planned.

Production passes `new Random()`, so demos differ slightly. Tests pass fixed seeds and check invariants over many seeds.

**Rationale**:
- Building a `RestoreFile` reuses restore's validation, its exercise get-or-create and its consent handling unchanged (R5).
- Generating rather than shipping a JSON fixture keeps the dates current (FR-007), and keeps the "realistic" rules readable as code with comments.
- `localNow` is server UTC now shifted by the visitor's offset, so "today" and "late afternoon" mean the visitor's day.

**Alternatives considered**:
- A committed backup JSON with dates shifted on load: the shift logic is about as long as the generator, and the data is harder to read and change.
- Using Europe/Helsinki via `TimeZoneInfo`: depends on tzdata in the runtime image and is wrong for visitors elsewhere.

## R5 — Insert through restore's core, not a second insert path

**Decision**: Split `NotebookRestore.RestoreAsync` into:

- `ImportAsync(RestoreFile file, int userId, bool privacyLifecycleEnabled, CancellationToken ct) → RestoreResponse`. This is the body that inserts rows. It requires a current transaction and touches nothing HTTP.
- `RestoreAsync`, which keeps the restore lock, the statement timeout and the HTTP result, and calls `ImportAsync`.

The demo start and reset call `DemoSeed.Build`, then `NotebookRestore.Validate`, which throws if the seed is invalid (that would be a bug), then `ImportAsync`.

**Rationale**: Restore already makes notebook data obey every rule (set status per page state, unique template names, optional-details consent with the row lock). Reusing it means FR-009 and FR-016 come for free: with the privacy flag on and no consent, the seed's details are dropped exactly as a restored backup's would be.

**Alternatives considered**: Building EF entities directly in `DemoSeed`. That would duplicate the insert logic, and the consent decision with it.

## R6 — Reset: `POST /account/demo/reset`

**Decision**:
- Body: `{ utcOffsetMinutes }`. Answer: `204`.
- Mapped only with the demo flag. It uses `RequireAuthorization()` and the existing per-account `restore` rate-limit policy.
- It is **not** under the shared filter. Like password change and deletion, it opens its own transaction and takes `AcquireExclusiveAsync` (one more entry on `LifecycleCoverageTests`' allow-list).
- Under the lock, it re-reads the user:
  - not a demo → 403 `not_a_demo`;
  - expired → 401;
  - otherwise it deletes the notebook (workouts, then templates, then exercises, the same order and helper as deletion, R8), imports a fresh seed and commits.
- On a lock timeout → 503 `temporarily_unavailable`.

The account row, token version, notice acknowledgement, consent and expiry are untouched (FR-010).

**Rationale**: Exclusive access is what makes "no request sees a half-replaced notebook" true (spec edge case). Shared access would let a concurrent page save land between the delete and the insert. The answer carries nothing personal, so no delivery guard is needed after the commit.

**Alternatives considered**: Delete and recreate the account. That would revoke the visitor's session and lose their privacy answers, against FR-010.

## R7 — Demo accounts can't use any password or email route

**Decision**:
- **Email routes**: the demo address has no `@`, so `AccountInput.IsValidEmail` rejects it. Register and password reset therefore answer 400 `invalid_request` before any lookup, and **no email can ever be queued to a demo address**.
- **Login**: looks the address up without a format check. A demo has no known password, so it already answers 401. As a second line of defence, login also answers the same 401 for a demo row after the password check (FR-012: indistinguishable from a wrong password).
- **Change password, export and delete**: answer 403 `demo_account` before the password check. This is a reason the PR 2 screens can explain, and it's safe because only the account's own session can reach these routes.

**Rationale**: Without the format choice, a `demo-…@demo.invalid` address would let register or password reset queue a message to a domain that bounces, hurting the sender's reputation. Making the address unaddressable removes that whole class of problems instead of guarding each route.

**Alternatives considered**: An `.invalid` address plus a demo check on each email route: four checks to keep in sync instead of none.

## R8 — Cleanup reuses `AccountDeletion`

**Decision**:
- **Worker**: `DemoCleanupWorker` (a `BackgroundService`, like `EmailOutboxWorker`), registered whatever the flag says, so demo accounts left behind when the flag is turned off are still removed. Every `Demo:CleanupIntervalMinutes` it selects up to 50 user ids with `demo_expires_at <= now()`, and for each calls a new `AccountDeletion.DeleteExpiredDemoAsync(int userId, CancellationToken ct)`.
- **What that method does**: it reads the current token version, takes the exclusive lock, re-checks under the lock that the row is still an expired demo, and runs the same delete statements and the same three evidence log lines as the user-requested deletion. The statements move into one private method that both paths call.
- **Errors**: a failure on one account is logged without details and the loop moves on. That is the worker boundary, so a narrow `catch` on database failures is allowed there.

**Rationale**: `AccountIdentityTests` permits only `AccountDeletion.cs` to remove user rows (specs/001 research R6 Q2d), and the evidence lines keep restore reconciliation correct for demo rows too. Taking the exclusive lock means an in-flight request finishes before its account disappears.

**Alternatives considered**: A bulk `DELETE FROM users WHERE demo_expires_at <= now()`: it breaks the identity rule and skips the lock and the evidence.

## R9 — `/auth/me` tells the screens it's a demo

**Decision**: `MeResponse` gains `DemoExpiresAt` (`DateTimeOffset?`), which is null for registered accounts. This is an additive field.

**Rationale**: The cover already calls `/auth/me`, so the PR 2 badge and reset button need no new request (FR-014).

## R10 — Documentation in PR 1

**Decision**: PR 1 updates the `PLAN.md` data model, REST API, Auth (demo accounts) and Milestones sections, and the README configuration table (`DEMO_MODE_ENABLED`, `Demo:*`, `DemoRateLimit:*`). `docs/ui/` waits for PR 2, which adds the screens.

**Rationale**: Principle VI: API and behaviour docs change in the PR that changes them. PR 1 changes no screens.
