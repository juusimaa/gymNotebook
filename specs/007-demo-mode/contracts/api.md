# API Contract: Demo Mode

**Feature**: [spec.md](../spec.md) | **Research**: [research.md](../research.md)

Both demo routes are mapped only when `DEMO_MODE_ENABLED` is exactly `"true"`. Otherwise they answer 404, like any unknown route. Both carry `Cache-Control: no-store`.

## POST /auth/demo

Starts a demo: creates a demo account, fills its notebook and signs the caller in.

Request:

```json
{ "turnstileToken": "…", "utcOffsetMinutes": 180 }
```

- `utcOffsetMinutes`: the browser's offset from UTC (`-new Date().getTimezoneOffset()`). Required, an integer from −840 to 840.
- `turnstileToken`: required when Turnstile is configured. The widget's action is `demo`.

| Status | Body | When |
|--------|------|------|
| 200 | `{ "token": "<jwt>" }` | Created and seeded. The same `AuthResponse` as login |
| 400 | `{ "code": "invalid_request" }` | Offset missing or out of range |
| 400 | `{ "code": "captcha" }` | Bot check failed or unavailable (fails closed). Nothing is created |
| 429 | — (`Retry-After`) | Per-IP `demo` policy (default 3 per hour) |
| 503 | `{ "code": "demo_unavailable" }` (`Retry-After`) | The number of live demo accounts is at `Demo:MaxLiveAccounts`. Nothing is created |

## POST /account/demo/reset

Replaces the caller's whole notebook with a newly generated one. Bearer token required.

Request:

```json
{ "utcOffsetMinutes": 180 }
```

| Status | Body | When |
|--------|------|------|
| 204 | — | Reset committed |
| 400 | `{ "code": "invalid_request" }` | Offset missing or out of range |
| 401 | — | No or invalid token, or the demo has expired |
| 403 | `{ "code": "not_a_demo" }` | The caller is a registered account. Nothing changes |
| 429 | — (`Retry-After`) | Per-account `restore` policy |
| 503 | `{ "code": "temporarily_unavailable" }` (`Retry-After`) | Waiting for exclusive access timed out. Nothing changed |

Kept: the account, its session, its notice acknowledgement and consent, and `demoExpiresAt`. Seeded titles, locations, notes and bodyweight are included only when the account has optional-details consent (or the privacy feature is off).

## Changes to existing routes

| Route | Change |
|-------|--------|
| `GET /auth/me` | Adds `demoExpiresAt` (ISO 8601 or `null`). Null for registered accounts |
| `POST /auth/login` | A demo row answers 401, identical to a wrong password |
| `POST /auth/change-password` | Demo account: 403 `{ "code": "demo_account" }`, before the password check |
| `POST /account/export` | Demo account: 403 `{ "code": "demo_account" }`, before the password check |
| `POST /account/delete` | Demo account: 403 `{ "code": "demo_account" }`, before the password check |
| `POST /auth/register`, `POST /auth/password-reset` | Unchanged. A demo address has no `@`, so it is already 400 `invalid_request` and no email is queued |
| Every authorized route | An expired demo's token answers 401, like a revoked token, through the bearer check and the lifecycle guard |
