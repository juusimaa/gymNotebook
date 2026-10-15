# Implementation Plan: Predefined Sessions

**Branch**: `006-predefined-sessions` | **Date**: 2026-10-15 | **Spec**: [spec.md](spec.md)

**Status**: Draft for owner review (Principle VII). Q1–Q5 resolved by the owner on 2026-10-15 (all as proposed). No frontend work starts before the `/impeccable` UI mock is approved (issue #165).

## Summary

Let a lifter write the page before the gym and only tick sets off at the rack.

- **Prepared page.** A prepared session is an ordinary `workouts` row that hasn't started: its date and start time are null until **Start** fills them in. So the editor, autosave, revision check, ownership, deletion and consent handling all carry over.
- **Set status.** Each set gains a status (`planned`, `done` or `skipped`), and only done sets count as training anywhere. Finishing turns what's still planned into skipped.
- **Templates.** Named, per-user templates live in three small tables mirroring the page structure. They're copied into prepared pages, never linked.
- **Backup.** The export moves to format version 2.

See [research.md](research.md), [data-model.md](data-model.md), [API contract](contracts/api.md), [UI contract](contracts/ui.md) and [quickstart.md](quickstart.md).

## Technical Context

**Language/Version**: unchanged: C# / .NET 10, TypeScript, React 19, Vite.

**Primary Dependencies**: none new.

**Storage**: PostgreSQL 18. Three migrations:
- `AddSetStatus`: `set_entries.status`, default `done`.
- `AllowPreparedWorkouts`: nullable `date`/`started_at` and two check constraints.
- `AddTemplates`: three tables, with a unique `(user_id, normalized_name)`.

**Testing**: xUnit + Testcontainers for:
- the status rules and transitions
- every metric filter
- prepared-page lifecycle and listing
- template CRUD, the name clash, and copy independence
- merge re-pointing
- account deletion
- export/restore v1 and v2

The existing suites must pass unchanged (SC-006). Vitest for the draft helpers. Owner phone walkthrough per [quickstart.md](quickstart.md).

**Target Platform**: unchanged. Phone-first web app on the existing UpCloud deployment.

**Project Type**: web application (`backend/` API + `frontend/` SPA).

**Performance Goals**: no new hot path. List and metric queries gain one predicate on `status`, inside the same single query per screen. There are no per-row queries (no N+1).

**Constraints**:
- ownership 404s
- the lifecycle filter on the new `/templates` group
- the optional-details consent on prepared pages' heading fields
- `Cache-Control` unchanged
- no new browser storage, with the draft storage format extended compatibly

**Scale/Scope**: a handful of prepared pages and templates per user. Neither list is paged.

## Constitution Check

| Principle | Status |
| --- | --- |
| I. Architecture | Pass. Minimal API routes in `Program.cs` (a `/templates` group beside `/workouts`), EF entities with bare FKs and downward navigation only, no new layers, interfaces or dependencies. `ExerciseNameNormalizer` is reused for template names; `IsUniqueViolation` for the clash. |
| II. Focused changes | Pass. Eight PRs below, each one reviewable alone. Nothing unrelated is refactored, and the normalizer keeps its name. |
| III. Learning | Pass. The two subtle parts each live in one small, commented place: the status rules (`SetStatusRules`, one table in code) and the derived page state (`WorkoutState.Of`). |
| IV. Verification | Pass. Every rule is tested against real Postgres, including the migration default and the check constraints. Migrations are reviewed before applying. |
| V. Security & privacy | Pass, with one item for review (R13). New routes are owner-scoped with 404s and run under the lifecycle filter. Prepared pages fall under the existing consent check and withdrawal because they are `workouts` rows. Templates and prepared pages are deleted with the account and included in the export. Template names are treated like exercise names, outside the optional-details consent; that call is recorded in the spec's Assumptions. |
| VI. Docs aligned | Planned. Each PR updates what it changes: `PLAN.md` (data model, API, frontend, a new milestone 15), `docs/ui/README.md` and `prototype.html` (the mock PR, then kept in sync), and spec 001/004's export contracts for format version 2. `README.md` has no new setting or command, so it changes only if the walkthrough finds something. |
| VII. Reviewed plans | This plan is a draft. Q1–Q5 were surfaced and decided by the owner, not by generation. |

*Post-design re-check*: unchanged. The design adds no layer, dependency or interface. The one cross-table rule (status by page state) is enforced in a single API helper rather than spread across routes.

## Design decisions

**D1 — Prepared = a workout that hasn't started** (R1). Date and start are null until `POST /workouts/{id}/start`. State is derived from the timestamps, never stored. Check constraints keep date and start paired and forbid an end without a start.

**D2 — Set status with a server-enforced table** (R2, R3). `planned | done | skipped`, default `done`. Which statuses each page state allows, and the transitions on start, finish and reopen, live in `SetStatusRules`, called by every set write route and by the workout PATCH.

**D3 — "Training" means done** (R4). History, last-time hints, session counts and the list's set count read done sets only. All existing data is done, so every number is unchanged.

**D4 — Own routes for the prepared list, create and start** (R5). The history list and its cursor stay as they are.

**D5 — Templates in their own tables, names unique per user** (R6, R7, R8). The unique index is the guard, and a 409 is the answer. Templates are copies in both directions. Merges re-point template blocks.

**D6 — Export format 2, restore reads 1 and 2** (R10, R11).

**D7 — The editor gains modes, not a fork.** `NewWorkout.tsx` already switches between new, in-progress and finished pages. Prepare becomes a fourth mode: explicit save, no done controls, **Start session** in the footer. The draft helpers (`newWorkoutDraft.ts`) gain `status` on a set row. `editorDraftStorage.ts` reads a stored row without `status` as `done`, so a draft written before the deploy survives it. The template editor reuses the block and set-row helpers but is its own screen, since it has no heading, no autosave and no status. Extracting shared block and row components out of the 2,200-line editor is done only as far as the template editor needs; it is not a general split.

## Delivery

Each backend PR updates `PLAN.md` for what it lands. Order matters where noted.

1. **UI mock** (`docs/ui/`). `/impeccable` mock of [contracts/ui.md](contracts/ui.md): spec text and prototype screens. Owner approval gates PRs 6–8. It can run in parallel with PRs 2–5.
2. **Set status** (backend). Migration `AddSetStatus`, `SetStatusRules`, `status` on set inputs and responses, finish and reopen transitions, done-only metrics and `notDoneSetCount`. The frontend keeps working unchanged because omitted status means done.
3. **Prepared pages** (backend). Migration `AllowPreparedWorkouts`, the prepared list, create and start routes, the started-only history list, nullable date/start in responses, and the frontend API types (`date`/`startedAt` nullable).
4. **Templates** (backend). Migration `AddTemplates`, the `/templates` routes, prepare-from-template, save-as-template, merge re-pointing, account deletion and lifecycle coverage.
5. **Backup v2** (backend + `backupCsv.ts`). Export version 2, restore 1 and 2, the field guide, and spec 001/004 contract notes.
6. **Done toggle and skipped sets** (frontend, after 1 and 2). In-progress done toggle, next set, finish summary, skipped on the session page and the finished editor, draft `status`.
7. **Prepared pages UI** (frontend, after 1 and 3). Prepare mode in the editor, the Prepared group on Sessions, Start.
8. **Templates UI** (frontend, after 1 and 4).

**Deploy order**: `main` deploys itself, so PR 5 (backup v2) must merge before PR 7 or PR 8. Until then no client can create prepared pages, planned sets or templates, so a version 1 export stays truthful. Template list and editor, prepare from template, save as template, the SC-007 entry point.

## Resolved questions (owner, 2026-10-15: all as proposed)

- **Q1 — Reopening a finished page** (R3). When a finished page is reopened (Finished field emptied), skipped sets become planned again so they can be ticked. Alternative: they stay skipped until marked done by hand. **Resolved: back to planned.**
- **Q2 — Restoring prepared pages** (R11). Prepared pages have no start time to deduplicate by, so a restore always adds them, and restoring the same file twice duplicates plans (never history). Alternative: skip prepared pages on restore and report the count. **Resolved: always add.**
- **Q3 — Incomplete planned sets** (R12). A saved planned set must have reps (and weight per the usual rule). The spec's edge case has been narrowed to say so. **Resolved: accepted.**
- **Q4 — Template edits from two devices** (R9). Last write wins; no revision on templates. **Resolved: accepted for now.**
- **Q5 — Rows added during a session** (R14). Start done, as today, on every in-progress page, prepared or not. Alternative: on a page that began as a plan, new rows start planned. **Resolved: start done.** The UI mock may still revisit it.

## Project Structure

### Documentation (this feature)

```text
specs/006-predefined-sessions/
├── spec.md
├── plan.md              # this file
├── research.md
├── data-model.md
├── quickstart.md
├── contracts/
│   ├── api.md
│   └── ui.md
├── checklists/requirements.md
└── tasks.md             # /speckit-tasks, not created here
```

### Source Code (repository root)

```text
backend/GymNotebook.Api/
├── Program.cs                 # set status on set routes, prepared + start routes, /templates group, merge re-point
├── Workout.cs                 # Date/StartedAt nullable
├── WorkoutState.cs            # new: derived prepared / in progress / finished
├── SetEntry.cs                # Status
├── SetStatus.cs, SetStatusRules.cs        # new
├── Template.cs, TemplateExercise.cs, TemplateSet.cs   # new entities
├── Template*Request/Response.cs           # new DTOs, one record per file as today
├── Data/AppDbContext.cs       # status converter + check, workout checks, template config
├── Migrations/                # AddSetStatus, AllowPreparedWorkouts, AddTemplates
├── AccountDeletion.cs         # delete templates before exercises
├── NotebookExport.cs, NotebookRestore.cs, ExportFieldGuide.cs   # format 2
backend/GymNotebook.Tests/
├── SetStatusTests.cs, PreparedWorkoutTests.cs, TemplateTests.cs   # new
├── ExerciseTests.cs, ExerciseHistoryTests.cs, WorkoutTests.cs     # done-only metrics, list changes
├── RestoreTests.cs, ExportTests.cs, DeletionTests.cs, LifecycleCoverageTests.cs
frontend/src/
├── api/workouts.ts            # status, nullable date/start, prepared + start calls
├── api/templates.ts           # new
├── screens/NewWorkout.tsx, newWorkoutDraft.ts, editorDraftStorage.ts   # prepare mode, done toggle
├── screens/WorkoutDetail.tsx, Sessions.tsx                             # skipped, Prepared group
├── screens/Templates.tsx, EditTemplate.tsx (+ .css)                    # new
├── screens/backupCsv.ts       # format 2
├── routes.tsx
docs/ui/README.md, docs/ui/prototype.html, PLAN.md
```

**Structure Decision**: the existing two-project web layout. New code follows the existing one-type-per-file convention in `GymNotebook.Api/` and colocated `*.test.ts` in `frontend/src/`.

## Complexity Tracking

No constitution violations to justify.
