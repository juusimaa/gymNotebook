# Implementation Plan: Durable Logging

**Branch**: `003-durable-logging` | **Date**: 2026-10-07 | **Spec**: [spec.md](spec.md)

**Status**: Plan merged in #127. The seven in the spec's Clarifications are the owner's (2026-10-07).

## Summary

Make a logged set as durable as ink, and a sign-in last a session. The editor saves in-progress pages by itself through the `PUT /workouts/{id}/exercises` it already uses, with a revision number on each workout so two devices can't silently overwrite each other. A small `POST /auth/token` renews a valid token up to a 12-hour cap, so a long session never meets the sign-in screen. No new dependency, no new browser storage, no new credential.

See [data-model.md](data-model.md), [API contract](contracts/api.md), [UI contract](contracts/ui.md) and [tasks.md](tasks.md).

## Technical Context

**Language/Version**: unchanged: C# / .NET 10, TypeScript, React 19, Vite.

**Primary Dependencies**: none new. Renewal reuses `JwtTokenFactory` and `System.IdentityModel.Tokens.Jwt`.

**Storage**: PostgreSQL. `workouts` gains `revision integer not null default 1`. One migration.

**Testing**: xUnit + Testcontainers for the revision rules and renewal (including the cap and revocation, with a fake `TimeProvider`, which the app already registers). Vitest for the save scheduler, the renewal timing and the cross-tab token rule, all as pure helpers with the browser pieces passed in, like `auth/invalidation.ts`. Owner walkthrough on a phone for SC-001.

**Constraints**: ownership 404s, the lifecycle filter, token-version revocation, `Cache-Control: no-store` on auth answers, no tokens in logs.

## Constitution Check

| Principle | Status |
| --- | --- |
| I. Architecture | Pass. Minimal API routes, one entity column, no new layers or interfaces. The save scheduler is a plain module in `screens/`, like `editorDraftStorage.ts`. |
| II. Focused changes | Pass. Five PRs, listed in [tasks.md](tasks.md). |
| III. Learning | Pass. The two subtle parts, the save queue and the renewal cap, are small pure functions with their reasoning in comments. |
| IV. Verification | Pass. The revision check and renewal are tested against real Postgres; the scheduler's timing with fake timers. |
| V. Security & privacy | Pass, with one decision to review (D5). Bearer JWTs and token-version revocation are kept. A renewal needs a valid, unexpired token, so it adds no way in. What changes is that a stolen token can be kept alive until the cap or a password change, instead of 30 minutes. No new storage, so P5 stands. |
| VI. Docs aligned | Pass. PLAN.md (Auth, API, Milestones), `docs/ui/README.md` (session editor), README.md (the new setting) change with the behaviour. PLAN.md's "No refresh tokens" decision is replaced by D4, not silently dropped. |
| VII. Reviewed plans | The plan was reviewed and merged in #127. |

## Design decisions

**D1 — Autosave reuses the existing save, not new per-set routes.** `PUT /workouts/{id}/exercises` already writes the whole page in one transaction. Sending the full page each time costs a few hundred bytes and keeps one write path, one validation and one source of truth. The per-set `POST`/`PATCH` routes would make the editor reconcile ids set by set, which is where autosave bugs come from. The existing `saveWorkout` in `NewWorkout.tsx` is split into a `persistDraft` function that both the buttons and the scheduler call.

**D2 — A save scheduler with one request in flight.** `screens/saveScheduler.ts`: a small state machine (`idle → waiting → saving → idle | failed`). An edit restarts the 2 s timer; a set becoming complete fires at once; a save in flight makes later edits wait for the next one. Failures back off 5 s, 15 s, then 60 s. Timers and the save call are passed in, so Vitest drives it with fake timers.

**D3 — Revision as a column, not Postgres `xmin`.** `xmin` (Npgsql's built-in row version) changes only when the `workouts` row itself is updated, and `PUT /exercises` changes only the child rows, so it would miss exactly the write that matters. An explicit `revision` integer is also easier to read in a learning project. Every write route increments it with a conditional `UPDATE … SET revision = revision + 1 WHERE id = @id AND user_id = @user AND (@expected IS NULL OR revision = @expected)` inside the existing transaction; zero rows updated means 404 (not found / not yours) or 409 (stale), told apart by a second lookup. The check and the bump are one statement, so two saves can't both pass.

**D4 — Renewal, not refresh tokens.** `POST /auth/token` takes the bearer token like any authenticated route, so revocation, suspension, the unconfirmed-account check and the link-token refusal all apply unchanged. It issues a token with the configured lifetime and copies the old token's `auth_time`. PLAN.md's "No refresh tokens" rationale still holds: there is no second credential, no rotation and no reuse detection to hand-write.

**D5 — The cap: 12 hours from the password.** Without a cap, a stolen token could be renewed forever until the password changes. `auth_time` is set where the password is proven (sign-in, change-password, reset), and renewal refuses once `now - auth_time > Jwt:RenewalCapHours`. Twelve hours covers the longest realistic gym day with a margin; it's a setting so it can move. *Owner, 2026-10-07: 12 h for now.*

**D6 — A refused renewal is 403, not 401.** The frontend treats every 401 as "session ended" and clears state. A renewal refused only because of the cap shouldn't end a session whose token is still good for up to 30 minutes, so it answers 403 `renewal_refused` and the frontend just stops trying; the next 401 ends the session as today. A stale token version still answers 401, through the existing bearer check, and ends the session at once.

**D7 — Renew at half-life while in use, and as the tab is hidden.** In `api/client.ts`, before a request: if the token is past half its lifetime, await one shared renewal first. Also on `visibilitychange` to visible, and on a one-minute check while the tab is visible, so a screen left open with nothing to save doesn't run out. As the tab is hidden, any token at least five minutes old renews with `keepalive`, so a locked phone sleeps on a nearly fresh token: at least 25 minutes on the bench, 30 when the renewal lands. That renewal is best effort (a page frozen at once may never store the answer) and isn't shared, so the editor's flush on the same event never waits for it. The five-minute floor keeps tab switching from spending the `auth` rate limit, which sign-in shares. *Owner test on Azure, 2026-10-08:* the first version renewed only on requests and on visibility, so 15–30 idle minutes, depending on when the token was last renewed, signed out, and "25 minutes on the bench" held only in the best case.

**D8 — Cross-tab: a renewed token isn't a session change.** `auth/invalidation.ts` reloads every other tab when the token changes, which would reload a tab mid-edit every 15 minutes. The rule becomes: removed token → end the session (as today); a token for another `sub` → reload (as today); same `sub` → nothing, since `getToken()` reads storage on every request anyway. Reading `sub` needs only the token's payload, which `auth/token.ts` already parses for its expiry check.

**D9 — A new page becomes its edit page after the first save.** Once the first autosave has created the page, the editor replaces its address with `/workouts/{id}/edit` and moves its draft to that route's key. Reloads, sign-in after expiry and **Continue logging** then all reach the same page, and the draft code has one case fewer (a "new page with an id"). The editor already behaves the same on both routes for an in-progress page.

**D10 — Conflict recovery keeps a trace.** On `409 page_changed` the editor stops autosaving, keeps the draft, and shows the conflict line with **Reload page**. Reloading loads the server's page and lists the sets that only existed here in the notice, rather than merging them. Merging set lists automatically is out of scope.

## Configuration

| Setting | Local (`.env`) | Azure | Notes |
| --- | --- | --- | --- |
| `Jwt__ExpiryMinutes` | 30 | 30 | unchanged; now each token's lifetime, renewed while in use |
| `Jwt__RenewalCapHours` | 12 (optional) | 12 (optional) | new, plain env; default 12 |

## Resolved questions (owner, 2026-10-07)

- **Q1 — The cap:** 12 hours, for now (D5).
- **Q2 — Save button:** dropped on in-progress pages. Autosave covers it, and the footer has one action, **Finish session**, which flushes first. Finished pages keep **Save changes**.
- **Q3 — Incomplete-row notice:** after 60 s, so it doesn't nag between typing the weight and the reps.
