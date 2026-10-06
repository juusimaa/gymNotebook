# HTTP Contract: Email Login and Open Signup

Draft for owner review, 2026-10-06. Proposed, not implemented.

## Common behavior

- Every route below is public (no bearer), in the existing `auth` group, answers with `Cache-Control: no-store`, and never logs request bodies, addresses, passwords or tokens.
- Email addresses are trimmed and lowercased before use. Blank or malformed input is 400 `invalid_request`; this reveals nothing about accounts.
- Link-token failures are 400 `{ "code": "expired" }` or 400 `{ "code": "invalid" }`. Never 401 (spec FR-009).
- Turnstile failure is 400 `{ "code": "captcha" }`. Rate limit is 429 with `Retry-After`.
- Mail is queued after the response is decided and is subject to the caps; a capped or failed send never changes the response.

## Changed routes

| Method/path | Request | Responses | Notes |
| --- | --- | --- | --- |
| POST /auth/register | `{ email, password, displayName, turnstileToken? }` | **202, empty**, always for valid input | Replaces 200 `AuthResponse` and 409. No token is returned: the account can't sign in until confirmed. `inviteCode` removed (PR 5). Rate limit `auth`. Turnstile action `signup`. |
| POST /auth/login | `{ email, password }` | 200 `AuthResponse`; 401 (unknown or wrong password); 403 `account_suspended`; 403 `email_not_verified` | Both 403s only after a correct password; suspension wins. Rate limit `auth`. |
| GET /auth/me | bearer | 200 `{ userId, displayName, email }` | `username` renamed `displayName` (O1). |

### Register: what the inbox gets

| State of the address | Account change | Email |
| --- | --- | --- |
| No account | Create unconfirmed account | Confirmation link |
| Confirmed account | None | "You already have an account" + sign-in link, no token |
| Unconfirmed account | None (submitted password discarded) | "Finish creating your account" + **reset** link |

## New routes

| Method/path | Request | Responses | Notes |
| --- | --- | --- | --- |
| POST /auth/verification | `{ email, password }` | 204 always | Resends the confirmation link only if the password is right and the account is unconfirmed. Rate limit `email-request`. |
| POST /auth/verify-email | `{ token }` | 200 `{ email }`; 400 `expired`/`invalid` | Idempotent; stamps `email_verified_at` the first time. Returns the address so the sign-in screen can pre-fill it. Rate limit `email-link`. |
| POST /auth/password-reset | `{ email, turnstileToken? }` | 202 always | Sends a reset link only to an existing account, confirmed or not. Rate limit `email-request`. Turnstile action `password_reset`. |
| POST /auth/password-reset/confirm | `{ token, newPassword }` | 200 `AuthResponse`; 400 `expired`/`invalid`/`invalid_request`; 403 `account_suspended`; 503 `temporarily_unavailable` | Exclusive lifecycle lock, as change-password. Sets the hash, bumps `TokenVersion`, stamps `email_verified_at` if null, returns a fresh token. Suspended: nothing changes. Rate limit `email-link`. |

## Removed

- `inviteCode` on register, the `INVITE_CODE` setting and its 403.
- 409 "username taken" on register.
