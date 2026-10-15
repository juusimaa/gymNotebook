# Quickstart: validating Predefined Sessions

Prerequisites: Docker running. Run commands from the repo root unless noted.

## Automated checks

```sh
dotnet test backend/GymNotebook.sln     # Testcontainers Postgres 18
dotnet format backend/GymNotebook.sln --verify-no-changes
cd frontend && npm run typecheck && npm run lint && npm run format:check && npm test
```

The backend tests must prove these against real Postgres. The scenario numbers refer to [spec.md](spec.md).

| Area | Proves |
| --- | --- |
| Set status | Existing sets read as `done` after the migration. A status the page state disallows is 400. Finish turns planned into skipped. Reopen turns skipped into planned. |
| Metrics | Planned and skipped sets are absent from history, `lastSet`, `firstSet` and `sessionCount`. An all-skipped page adds nothing (SC-004). Existing tests pass unchanged (SC-006). |
| Prepared pages | Created without date or start. Listed only in `/workouts/prepared`. Absent from `/workouts`; a prepared id as `before` is 400. `/start` works once, then 409 `already_started`. A stale revision is 409 `page_changed`. Another user's page is 404. |
| Templates | CRUD. Name clash after normalizing (`"Push A"` vs `" push  a "`) is 409 and writes nothing, while another user may use the name. Prepare copies, and later edits don't cross. Save-as-template copies any page. Exercise merge re-points template blocks. Another user's template is 404. |
| Account | Account deletion removes templates and prepared pages. Optional-details withdrawal clears prepared pages' details. Lifecycle coverage includes the `/templates` routes. |
| Backup | v2 export round-trips prepared pages, statuses and templates. A v1 file restores as all `done`. Invalid v2 files give `backup_invalid` without writes. |

Vitest covers the draft helpers: status defaults, the next-set rule, the finish summary count, and old stored drafts without `status` read as `done`.

## Manual walkthrough (phone, local stack)

```sh
docker compose up --build
```

1. **Story 1.** Prepare a session with two exercises and three sets each, then Save plan. Open Sessions on a second browser: it is under Prepared, not in history, and not on Progress.
2. **Story 2.**
   - Start it. The date line shows today and now.
   - Tap done on two sets. Each is one tap with no typing (SC-001), and the save state reads "Saved".
   - Close the tab, reopen on the second browser: those two are done.
3. **Story 3.** Change one planned weight, mark it done, and add a new exercise with one set. All are kept.
4. **Finish.** Finish with sets still planned. The confirmation names how many will be skipped. The session page shows them as skipped, and Progress ignores them (SC-008).
5. **Story 4.**
   - Save the session as template "Push A".
   - Try a second template named " push a ": refused inline.
   - From the cover, prepare from "Push A" in at most three taps (SC-007).
   - Change a weight in the new page. The template is unchanged.
6. **Backup.** Export, restore into a fresh account: prepared pages, skipped sets and templates come back as they were.
