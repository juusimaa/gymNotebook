# Quickstart: Email Login and Open Signup

Draft, 2026-10-06. How to exercise the feature locally once it is implemented, and the one destructive operator step.

## Local development

1. Existing local accounts can't survive the migration (plan D11). Reset the database: `docker compose down -v`.
2. `.env`: leave `Email__Backend=console`, `TURNSTILE_SECRET_KEY` empty, `INVITE_CODE` gone.
3. `docker compose up --build`, create an account on `http://localhost:3000` (or the Vite dev server) in a phone-sized browser window.
4. The confirmation link is printed by the API: `docker compose logs backend | grep verify-email`. Open it in the same window.
5. To try Turnstile locally, use Cloudflare's always-pass test pair: site key `1x00000000000000000000AA`, secret `1x0000000000000000000000000000000AA`, `TURNSTILE_HOSTNAMES=example.com`.

## Production wipe (operator, once, before PR 3 deploys)

Owner decision 2026-10-06: every existing account is deleted rather than migrated. Run against the production Neon branch from a trusted machine, after confirming the row counts are the expected owner-only data:

```sql
BEGIN;
SELECT count(*) FROM users;          -- record the number in the release notes
DELETE FROM workouts;                -- blocks and sets cascade
DELETE FROM exercises;
DELETE FROM users;
COMMIT;
```

Same order as `AccountDeletion.cs`. If PR 3 deploys first by mistake, its migration fails on the non-empty `users` table and the old revision keeps serving; run the wipe and redeploy.

Neon's six-hour history window means a point-in-time restore could bring these rows back only within six hours of the wipe; specs/001's restore procedure does not apply to them because they were not deleted through `/account/delete`.

## Verification checklist

- [ ] Signup on a phone: check-inbox screen, email arrives from `mail.gymnotebook.fit`, link opens and confirms.
- [ ] Sign in before confirming: check-inbox state, resend works.
- [ ] Signup again with the same address: identical screen; "already have an account" email.
- [ ] Forgot password → link → new password → signed in; old password refused.
- [ ] Turnstile visible and fits a 320px-wide screen in light and dark themes.
- [ ] `az containerapp show` for the API lists no `INVITE_CODE`.
