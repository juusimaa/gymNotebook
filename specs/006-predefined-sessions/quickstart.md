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
| Start from template (R15) | Creates an in-progress page dated and started as sent, every set planned. Missing date or start is 400. Another user's template is 404. (PR 10: existing prepared pages become templates in the migration and on restore.) |
| Templates | CRUD. Name clash after normalizing (`"Push A"` vs `" push  a "`) is 409 and writes nothing, while another user may use the name. Start copies, and later edits don't cross. Save-as-template copies any page. Exercise merge re-points template blocks. Another user's template is 404. |
| Account | Account deletion removes templates and prepared pages. Optional-details withdrawal clears prepared pages' details. Lifecycle coverage includes the `/templates` routes. |
| Backup | v2 export round-trips prepared pages, statuses and templates. A v1 file restores as all `done`. Invalid v2 files give `backup_invalid` without writes. |

Vitest covers the draft helpers: status defaults, the next-set rule, the finish summary count, and old stored drafts without `status` read as `done`.

## Manual walkthrough (phone, local stack)

```sh
docker compose up --build
```

1. **Story 1.** Write a template with two exercises and three sets each, then Save. Open Templates on a second browser: it is there, and nothing of it is in history or on Progress.
2. **Story 2.**
   - From Sessions, **From template**, then the template. The session opens in progress; the date line shows today and now, and every set is to do.
   - Tap done on two sets. Each is one tap with no typing (SC-001), and the save state reads "Saved".
   - Close the tab, reopen on the second browser: those two are done.
3. **Story 3.** Change one planned weight, mark it done, and add a new exercise with one set. All are kept.
4. **Finish.** Finish with sets still planned. The confirmation names how many will be skipped. The session page shows them as skipped, and Progress ignores them (SC-008).
5. **Story 4.**
   - Save the session as template "Push A".
   - Try a second template named " push a ": refused inline.
   - From the cover, start a session from "Push A" in at most three taps (SC-007). **Start** on the template list and **Start session** in the template editor do the same.
   - Change a weight in the new page. The template is unchanged.
6. **Backup.** Export, restore into a fresh account: skipped sets and templates come back as they were.
