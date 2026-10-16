# Quickstart: Demo Mode (validation)

**Feature**: [spec.md](spec.md) | **Contract**: [contracts/api.md](contracts/api.md)

## Automated checks (PR 1)

```sh
dotnet format backend/GymNotebook.sln --verify-no-changes
dotnet test backend/GymNotebook.sln   # Docker required (Testcontainers Postgres 18)
```

The demo tests live in `backend/GymNotebook.Tests/Demo*Tests.cs`; the test list is in [tasks.md](tasks.md).

## Manual check against the local stack

1. In `.env` (or user-secrets), set `DEMO_MODE_ENABLED=true`. For Turnstile, use Cloudflare's always-pass test keys (see `Turnstile.cs`) or leave it unconfigured.
2. Start the stack: `docker compose up --build`.
3. Start a demo:

   ```sh
   curl -s -X POST localhost:8080/auth/demo -H 'Content-Type: application/json' \
     -d '{"utcOffsetMinutes":180}'
   ```

   Expected: `{"token":"…"}`.
4. With `Authorization: Bearer <token>`:
   - `GET /auth/me` returns `demoExpiresAt` about 24 h ahead.
   - `GET /workouts?limit=50` returns about 36 finished pages and one unfinished page from today.
   - `GET /templates` returns three templates.
   - `GET /exercises/{id}/history` for Back Squat shows a rising series with one dip.
5. `POST /account/demo/reset` with `{"utcOffsetMinutes":180}` returns 204, and `GET /workouts` shows a fresh notebook.
6. Expiry: run `UPDATE users SET demo_expires_at = now() - interval '1 minute' WHERE email LIKE 'demo:%';`. The next request returns 401. After the next cleanup run (or a restart with `Demo:CleanupIntervalMinutes=1`), the row is gone.
7. Flag off: unset `DEMO_MODE_ENABLED` and restart. `POST /auth/demo` returns 404.

The screens (PR 2) are validated in that PR's quickstart addendum.
