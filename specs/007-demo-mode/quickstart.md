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

## Screens (PR 2)

With `DEMO_MODE_ENABLED=true` in the root `.env` (or `DEMO_MODE_ENABLED=true docker compose up --build`), open `http://localhost:3000/login`:

8. `config.js` carries `window.__DEMO_MODE_ENABLED__ = "true"`, and **Try the demo** shows under "New here? Create an account".
9. **Try the demo** → **Start the demo** (with the Turnstile test keys, the widget first): the cover shows "Demo Lifter", "Demo · ends tomorrow at HH.mm" and no `demo:` address.
10. **Open the notebook**: the notice gate, then Sessions with about twelve weeks of pages and one in progress today.
11. **Reset demo data** → the confirmation (with the optional-details hint while details aren't allowed) → **Reset demo data**: "Demo data reset. The notebook is new again."
12. **Backup & restore** shows "Backups aren't available in a demo"; **Privacy & account** says a demo can't be deleted by hand; `/change-password` submitted answers "This isn't available in a demo."
13. **Leave demo** → **Leave demo** returns to sign-in.
14. Flag off (`DEMO_MODE_ENABLED` unset, rebuild): no **Try the demo** button.
