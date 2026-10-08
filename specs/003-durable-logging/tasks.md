# Tasks: Durable Logging

**Status**: Plan merged in #127; PR 2 implementation complete, awaiting review. AI implements by default; the owner names any task they will write themselves.

## PR 1 — Plan (merged in #127)

- [x] T001 `specs/003-durable-logging/` spec, plan, data model, contracts and tasks.

## PR 2 — Token renewal (backend)

- [x] T010 `auth_time` claim in `JwtTokenFactory.CreateToken`, set at sign-in, change-password and password reset; `Jwt:RenewalCapHours` with default 12.
- [x] T011 `POST /auth/token` in the `auth` group with the `auth` rate limit and `Cache-Control: no-store` (contracts/api.md).
- [x] T012 Tests: `RenewToken_ValidToken_ReturnsFreshTokenWithSameAuthTime`, `RenewToken_PastCap_Returns403RenewalRefused`, `RenewToken_WithoutAuthTime_Returns403`, `RenewToken_AfterPasswordChange_Returns401`, `RenewToken_LinkToken_Returns401`, `RenewToken_Expired_Returns401`, `RenewToken_Suspended_Returns403` (fake `TimeProvider`).
- [x] T013 PLAN.md → Auth: replace "No refresh tokens" with the renewal decision (D4–D6); README.md configuration table.

## PR 3 — Token renewal (frontend)

- [ ] T020 `auth/token.ts`: read `sub` and the lifetime from the payload; `shouldRenew(token, now)` (past half-life, not expired).
- [ ] T021 `api/client.ts`: one shared renewal before a request when `shouldRenew`; on `visibilitychange`; a 403 `renewal_refused` stops renewal for this token.
- [ ] T022 `auth/invalidation.ts`: a same-`sub` token change doesn't reload (D8).
- [ ] T023 Vitest for T020–T022; update `editorDraftStorage.ts` and `invalidation.ts` comments that say "30 minutes with no refresh".

## PR 4 — Workout revision (backend)

- [ ] T030 Migration `AddWorkoutRevision`; `Revision` on `Workout`; `revision` in workout responses.
- [ ] T031 Conditional increment on every write route, with optional `expectedRevision` and `409 page_changed` (D3, contracts/api.md).
- [ ] T032 Tests: increments on each route; stale `expectedRevision` → 409 and nothing written; missing `expectedRevision` → accepted; another user's page with any revision → 404; two concurrent PUTs with the same revision → exactly one succeeds.
- [ ] T033 PLAN.md → API and data model.

## PR 5 — Autosave (frontend)

- [ ] T040 `screens/saveScheduler.ts` (D2) with Vitest on fake timers: debounce, immediate on complete set, single flight, backoff, flush, the 60 s incomplete-row notice.
- [ ] T041 `persistDraft` extracted from `saveWorkout`; complete sets only (FR-002); sends `expectedRevision`, keeps the returned one.
- [ ] T042 Editor wiring: autosave on in-progress pages, the date-line status (contracts/ui.md), no Save button on in-progress pages, Finish flushes first.
- [ ] T043 First save of a new page: create, then replace the address with `/workouts/{id}/edit` and move the draft key (D9).
- [ ] T044 Cancel on a saved new page: the tear-out confirmation (contracts/ui.md).
- [ ] T045 409 and 404 handling (D10).
- [ ] T046 `docs/ui/README.md` session editor, prototype note, PLAN.md milestone 13 entry.

## Owner checks

- [ ] T050 SC-001 walkthrough on a phone: a 90-minute session with no Save and no sign-in.
- [ ] T051 Two-device check (Story 3) on the deployed site.
