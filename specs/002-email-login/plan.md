# Implementation Plan: Email Login and Open Signup

**Branch**: `002-email-login` | **Date**: 2026-10-06 | **Spec**: [spec.md](spec.md)

**Status**: Draft for owner review (constitution Principle VII). Decisions D1–D12 below are proposals until this plan is merged.

## Summary

Replace username sign-in and the invite code with email sign-in, confirmation before first sign-in, password reset by email, and the abuse protections that make an open signup safe: enumeration-safe answers, per-address and app-wide email caps, and Cloudflare Turnstile on the two routes that email an address nobody has proven yet. The design follows `docker-subscription-tracker` (milestone 9 + `effd8ef`) and is translated into this project's Minimal API, EF Core and React Router idiom. Existing accounts are wiped rather than migrated.

See [data-model.md](data-model.md), [API contract](contracts/api.md), [UI contract](contracts/ui.md) and [tasks.md](tasks.md).

## Technical Context

**Language/Version**: unchanged — C# / .NET 10, TypeScript, React 19, Vite.

**Primary Dependencies**: no new NuGet or npm packages. Resend and Turnstile are each one HTTPS call made with `HttpClient` (`IHttpClientFactory`, already in the framework). Link tokens reuse `System.IdentityModel.Tokens.Jwt`, which `JwtTokenFactory` already uses. The Turnstile widget is Cloudflare's script loaded at runtime, not a package.

**Storage**: PostgreSQL. `users` gains `email` and `email_verified_at`; new `email_sends` table. One migration, applied after the operator wipe.

**Testing**: xUnit + Testcontainers as today. A `memory` email backend captures messages so tests read links out of them. Turnstile's HTTP call is replaced through `HttpClient` with a stub handler. Frontend helpers in Vitest. Owner walkthrough on a real phone.

**Target Platform**: mobile browsers only. No desktop layouts are designed or reviewed.

**Constraints**: preserve specs/001 lifecycle coordination (password reset takes exclusive access like change-password), suspension semantics, ownership 404s, `Cache-Control: no-store` on account responses, and no secrets or tokens in logs.

## Constitution Check

| Principle | Status |
| --- | --- |
| I. Architecture | Pass. Minimal API routes in `Program.cs`'s `auth` group, entity + migration, no new layers. `IEmailSender` has three implementations (console, memory, Resend), which is the concrete reason Principle I asks for. |
| II. Focused changes | Pass. Five PRs, listed in [tasks.md](tasks.md). |
| III. Learning | Pass. Each PR is reviewable alone; comments explain the anti-enumeration branches, which are the non-obvious part. |
| IV. Verification | Pass. Real-Postgres tests for every route and cap; the migration's failure on a non-empty table is itself tested. |
| V. Security & privacy | **Requires the 3.0.0 amendment in this PR**: it names the invite-code gate as a control to preserve and lists email collection as something not to invent. The amendment replaces the gate with the protections in FR-003, FR-011–FR-016 and FR-019–FR-020. |
| VI. Docs aligned | Pass. PLAN.md, README.md, `docs/ui/` and `docs/privacy/` change in the PR whose behavior they describe. |
| VII. Reviewed plans | This document is the draft under review. |

## Design decisions

**D1 — Password sign-in with email as identifier, not magic links.** Same as the reference. A magic link would make every sign-in depend on email delivery and on the link opening in the browser the user actually uses. On phones that is often a different browser from the one the app was used in, or a home-screen app that has its own storage.

**D2 — Link tokens are JWTs with a `purpose` claim; no token table.** `JwtTokenFactory` gains `CreateLinkToken(user, purpose, lifetime)`. Confirmation carries `{ sub, purpose: "verify", email }`; the email must still match when used, so a token minted for an address that changed can't confirm another. Reset carries `{ sub, purpose: "reset", email, tv }`; the email must match too, so a link can't reach another account that later got the same id (a restore rewinds the sequence). *Amended in PR 4: the plan had reset without `email`.* The bearer handler's `OnTokenValidated` fails any token that has a `purpose` claim, so a link can never be used as a session. A separate `Jwt:LinkSecret` is not introduced: the purpose check is the separation, as in the reference.

**D3 — A reset is single-use through `TokenVersion`.** Completing a reset bumps `TokenVersion`, which makes the link's `tv` stale (single use) and revokes every session (FR-007). It runs through the same exclusive-lock path as `POST /auth/change-password`, which is extracted into one shared helper rather than duplicated.

**D4 — Enumeration-safe answers, with the difference sent to the inbox.** As in the reference: 202 for every signup; the three emails of spec Story 1; dummy BCrypt on the branches that would otherwise skip hashing; mail sent after the response. A concurrent duplicate signup surfaces as a unique-violation `DbUpdateException` and is handled as "existing account".

**D5 — Unconfirmed accounts are rejected at sign-in and by the bearer check.** 403 `email_not_verified` only after a correct password. The bearer check rejects unconfirmed accounts too. Today that check reads `TokenVersion` in `OnTokenValidated` and again in `LifecycleFilter`; both select `EmailVerifiedAt` as well. Belt and braces: no code path mints a session for an unconfirmed account in the first place.

**D6 — Email after the response, through an in-process queue.** A bounded `Channel<EmailMessage>` read by a `BackgroundService`. The handler writes to the channel and returns; the service sends with a 10 s timeout and logs failures without the address. The API scales to zero, so a message queued while the container is stopping can be lost; the user can resend, and the reference accepted the same trade-off.

**D7 — Three email backends behind `IEmailSender`.** `Email:Backend` = `console` (logs the message; the app refuses to start with it in Production, since the message contains a live link), `memory` (tests; also refused in Production), `resend` (requires `RESEND_API_KEY`, `EMAIL_FROM`, `APP_URL`, checked at startup). *Amended in PR 2 (owner, 2026-10-06):* the rule was "Development only", but Compose runs the API outside Development to keep Scalar and the developer exception page off, which would have stopped `docker compose up`. The check is now "not in Production" (Azure's environment, ASP.NET Core's default when none is set), and Compose sets `ASPNETCORE_ENVIRONMENT=Local`.

**D8 — Caps stored as keyed hashes.** `email_sends(recipient_hash, sent_at)`. The hash is HMAC-SHA256 of the lowercased address, keyed with `Jwt:Secret`, so a database read reveals no addresses and a rainbow table needs the key. Claim a slot inside one transaction: delete rows older than 24 h, count per address and in total, insert. Two requests in parallel may both pass at the edge of the cap; at this scale one extra email is acceptable, and the transaction keeps it to that.

**D9 — Turnstile is a small static helper over `HttpClient`, fail closed.** `Turnstile:SecretKey` unset turns it off (local dev and tests); when set, `Turnstile:Hostnames` must be non-empty or the app refuses to start. Verification requires `success`, a listed hostname and the expected `action` (`signup` / `password_reset`). Cloudflare's test keys are honoured the same way as in the reference.

**D10 — Links use the URL fragment, read by dedicated public routes.** Unlike the reference, which has no router and used `?verify=`. Here `/verify-email` and `/reset-password` are public React Router routes. The token sits after `#`, which browsers never send to a server or put in a `Referer`. The screen reads it once and calls `history.replaceState` to drop it.

**D11 — Wipe, then migrate.** The migration adds `email` as `NOT NULL` without a default. PostgreSQL rejects that on a non-empty table, so a forgotten wipe stops the deploy instead of inventing addresses (FR-022). Production wipe: an operator SQL step in [quickstart.md](quickstart.md): one transaction deleting workouts, then exercises, then users, the same order `AccountDeletion.cs` uses (blocks reference exercises with `ON DELETE RESTRICT`, so a bare `DELETE FROM users` can fail on the cascade). It is deliberately not code: `AccountIdentityTests` permits only `AccountDeletion.cs` to remove user rows, and that rule stays. Locally: `docker compose down -v`.

**D12 — Rate limits.** Keep the `auth` policy (10/60 s per IP) on sign-in and signup. Add `email-request` (5/hour per IP) on resend and reset request, and `email-link` (10/hour per IP) on confirm and reset confirm. All key on the client IP already resolved by `FORWARDED_HEADERS_ENABLED`, which production has on since specs/001 T075 — the same problem the reference fixed in `48bc264`, already solved here.

## Configuration

| Setting | Local (`.env`) | Azure | Notes |
| --- | --- | --- | --- |
| `Email__Backend` | `console` | `resend` | plain env |
| `RESEND_API_KEY` | empty | Container Apps secret via `secretRef` | new GitHub secret → `deploy.yml` → Bicep `@secure()` param |
| `EMAIL_FROM` | any | `Gym Notebook <no-reply@mail.gymnotebook.fit>` | plain env |
| `APP_URL` | `http://localhost:5173` | `https://gymnotebook.fit` | base of links |
| `EMAIL_DAILY_CAP` | 90 | 90 | optional |
| `TURNSTILE_SECRET_KEY` | empty (off) | secret via `secretRef` | |
| `TURNSTILE_HOSTNAMES` | — | `gymnotebook.fit` | plain env |
| `TURNSTILE_SITE_KEY` (frontend) | Cloudflare test key or empty | runtime `config.js` | public value, served like `API_URL` |
| `INVITE_CODE` | **removed** | **removed** (Bicep param, secret, env, GitHub secret) | |

## Go-live order (operations)

1. Create the Resend account, add the `mail.gymnotebook.fit` sending domain in the **EU (Ireland, `eu-west-1`)** region, verify it (SPF, DKIM, DMARC at Cloudflare DNS), turn off open/click tracking, add `RESEND_API_KEY` to GitHub secrets.
2. Supplier review for Resend and Turnstile recorded in `docs/privacy/suppliers.md`.
3. **Wipe** production accounts (quickstart step), then merge PR 3. The invite code still gates signup during PRs 3–4, so the service never runs open without the protections.
4. Create the Turnstile widget for `gymnotebook.fit`, add its keys, merge PR 5 (which removes the invite code and so opens signup).
5. Delete the `INVITE_CODE` GitHub secret.
6. Owner walkthrough on a phone: signup → confirm → sign in → forgot password → reset (SC-001).

## Resolved questions (owner, 2026-10-06)

- **O1 — Column name: rename.** `Username` becomes `DisplayName` (column `display_name`, API field `displayName`) in PR 3, which already rewrites the register, `/auth/me` and export shapes. The migration renames the column rather than dropping and re-adding it.
- **O2 — Interim without a notice: accepted.** Email is collected before specs/001's notice is live in production. Recorded in the processing decision in PR 3.
- **O3 — Resend region: EU, Ireland (`eu-west-1`).** The sending domain is created in that region. The supplier review still records Resend's own processing and any onward transfers (support access, subprocessors), since the sending region does not settle those.
