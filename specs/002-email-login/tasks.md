---

description: "Task list for the Email Login and Open Signup feature"
---

# Tasks: Email Login and Open Signup

**Input**: [spec.md](spec.md), [plan.md](plan.md), [data-model.md](data-model.md), [contracts/](contracts/), [quickstart.md](quickstart.md)

**Status**: Draft, 2026-10-06, for owner review (constitution Principle VII). Per Principle III, AI implements by default unless the owner says they will write a part.

**Tests**: Required for every route and cap (Principle IV). Test names follow `MethodName_Scenario_ExpectedResult`. Write each PR's tests first.

## Format

- **[P]**: can run in parallel with the previous task
- **(owner)** / **(operator)**: a human decision or operational action, not code

---

## PR 1 — Plan (this PR)

- [x] T001 Spec, plan, data model, contracts, quickstart, tasks in `specs/002-email-login/`.
- [x] T002 Constitution 3.0.0: Principle V and Q2 amended.
- [x] T003 PLAN.md milestone 12 entry; "Deliberately out of scope" and Open items point to it. AGENTS.md current status.
- [x] T004 (owner) Answer O1–O3 in plan.md: rename, interim accepted, Resend EU (2026-10-06).
- [x] T005 (owner) Review and merge.

## PR 2 — Email plumbing (nothing user-visible)

- [x] T010 `EmailSend` entity, `AppDbContext` config, migration `AddEmailSends`; review the generated migration.
- [x] T011 `IEmailSender` + `ConsoleEmailSender`, `MemoryEmailSender`, `ResendEmailSender` (`HttpClient`, 10 s timeout); startup validation per plan D7 (console only in Development; resend requires key, from, app URL).
- [x] T012 `EmailOutbox`: bounded `Channel` + `BackgroundService` that sends and logs failures without addresses (plan D6).
- [x] T013 `EmailCaps.TryClaimAsync`: HMAC recipient hash, prune > 24 h, per-address 5 and daily `EMAIL_DAILY_CAP`, one transaction (plan D8).
- [x] T014 Email templates as plain C# (text + minimal HTML): confirmation, already-registered, finish-signup, password reset.
- [x] T015 Tests: caps per address and per day, hash stores no address, pruning, console backend refused outside Development, template links use `APP_URL` and the fragment form.
- [x] T016 [P] Config plumbing: `.env.example`, `docker-compose.yml`, `infra/main.bicep` + `container-app-api.bicep` (`resend-api-key` secret, plain env values), `deploy.yml` parameter. README configuration table.
- [x] T017 (operator) `RESEND_API_KEY` GitHub secret exists **before PR 2 merges**: the deploy passes it to Bicep, and the API refuses to start in Azure without it. Moved forward from T031; the sending domain can still be verified before PR 3, since PR 2 sends nothing.

## PR 3 — Email accounts (invite code still on)

- [ ] T020 (operator) Production wipe from quickstart.md, immediately before this PR deploys. Record the row count.
- [x] T021 `User.Email`, `EmailVerifiedAt`, rename `Username` → `DisplayName` and drop its unique index; migration `AddUserEmail`; test that it fails on a non-empty `users` table.
- [x] T022 `JwtTokenFactory.CreateLinkToken` / `ReadLinkToken`; bearer `OnTokenValidated` rejects `purpose` tokens and unconfirmed accounts; `LifecycleFilter` check includes `EmailVerifiedAt`.
- [x] T023 Register by email → 202, three inbox branches, dummy hash, unique-violation race handled as existing account.
- [x] T024 Login by email; 403 `email_not_verified` after password, suspension first.
- [x] T025 `POST /auth/verification`, `POST /auth/verify-email`; rate-limit policies `email-request`, `email-link`.
- [x] T026 Export + field guide: `email`, `emailVerifiedAt`, `username` → `displayName`; `MeResponse`; cover reads `displayName`.
- [x] T027 Update existing tests and fixtures that seed users or register (`GymNotebookFactory` helpers, `AccountIdentityTests`, `DeletionConcurrencyTests`, rate-limit tests) to the email shape. Test helper: register → read link from `MemoryEmailSender` → confirm.
- [x] T028 Tests: every acceptance scenario of Stories 1–2, enumeration (identical status/body across branches), link tokens rejected as bearer and across purposes, expired/tampered tokens.
- [x] T029 Frontend: `api/auth.ts` (register, login, resend, verifyEmail), `authErrors.ts`, Login modes Sign in / Create account / Check your inbox, `/verify-email` route, cover email line. Vitest for the fragment reader and error mapping.
- [x] T030 Docs: PLAN.md Auth/REST/Data model, README, `docs/ui/` spec + prototype, `docs/privacy/` processing decision (email added to the account purpose, field list, necessity text), rights-requests (the address now identifies the account), retention (`email_sends` 24 h; Resend's log retention), suppliers (Resend row), notice candidate.
- [ ] T031 (operator) Resend domain in EU (Ireland) region + DNS + tracking off + GitHub secret, before merge. (owner) Resend supplier review, including onward transfers.

## PR 4 — Password reset

- [ ] T040 Extract change-password's exclusive-lock update into a shared helper; `POST /auth/password-reset` and `/confirm` use it (plan D3).
- [ ] T041 Tests: Story 3 scenarios, single use, sessions revoked, unconfirmed account confirmed by reset, suspended account unchanged, rate limit.
- [ ] T042 Frontend: Forgot mode + Reset sent state, `/reset-password` route, docs/ui.

## PR 5 — Turnstile and removing the invite code (opens signup)

- [ ] T050 `Turnstile.VerifyAsync` over `HttpClient`, fail closed; startup check that hostnames are set when the secret is; wired into register and reset request.
- [ ] T051 Tests (stubbed `HttpMessageHandler`): off by default, required when on, wrong hostname, wrong action, test keys, no hostnames, Cloudflare unreachable.
- [ ] T052 Frontend: Turnstile component (flexible size, theme auto, remount per attempt), site key in runtime `config.js` like `API_URL`.
- [ ] T053 Remove the invite code: `Program.cs`, `RegisterRequest`, `InviteCodeGymNotebookFactory` and its tests, base fixture pin, `TwoHostGymNotebookFixture`, Login copy ("invite-only"), `authErrors`, `client.ts` comment, `.env.example`, compose, Bicep param/secret/env, `deploy.yml`, README, PLAN.md, PRODUCT.md, docs/ui, docs/privacy.
- [ ] T054 Docs: suppliers (Cloudflare row: Turnstile makes it an HTTP recipient on two screens), notice candidate, PLAN.md milestone 12 marked done.
- [ ] T055 (operator) Go-live steps 4–5 in plan.md. (owner) Phone walkthrough, SC-001.
