# Research: Predefined Sessions

Each item records a design question the spec left to planning, what was chosen, and what else was weighed. Items marked **owner** were product behaviour this plan proposed; the owner accepted all of them on 2026-10-15 ([plan.md](plan.md#resolved-questions-owner-2026-10-15-all-as-proposed)).

## R1 — A prepared session is a workout row, not a separate record

**Decision**: A prepared session is a `workouts` row whose `date` and `started_at` are null. Starting it fills them in. The page's state is derived from its timestamps, as "in progress" already is (`ended_at` null):

| `started_at` | `ended_at` | State |
| --- | --- | --- |
| null | null | prepared |
| set | null | in progress |
| set | set | finished |

**Rationale**: The spec says the prepared page and the logged page are the same page at different moments. One row means starting is an update, not a copy, so the id, the blocks, the sets and the revision all carry over, and the editor, autosave, revision check, ownership 404s, account deletion and optional-details withdrawal all work on prepared pages without new code. Deriving the state keeps the established rule that nothing stores what timestamps already imply (`PLAN.md`: "duration is derived, not stored").

**Alternatives considered**:
- *A `prepared_sessions` table copied into `workouts` on start.* That means a second copy of blocks and sets, a second editor path and a second ownership check, and the copy on start would need its own transaction and conflict story. Rejected as more code for the same behaviour.
- *Keep `date`/`started_at` NOT NULL with placeholder values plus an `is_prepared` flag.* Fewer null checks, but the row would claim a start time that never happened, and export would show it. `PLAN.md` keeps `date` separate from `started_at` precisely so the stored values mean what the lifter means. Rejected.
- *An explicit `status` column on `workouts`.* It would duplicate what the timestamps say and could disagree with them. Rejected.

## R2 — Set state is a column with three values

**Decision**: `set_entries.status` is `text not null default 'done'`, with a check constraint for `'planned' | 'done' | 'skipped'`. In C# it is an enum `SetStatus` mapped through a value converter to those lowercase strings. The migration default makes every existing set `done` (FR-012) with no data step.

**Rationale**: Readable in SQL, in the export file and in the API. A default of `done` keeps every existing client and test that doesn't send a status behaving as today (FR-011).

**Alternatives considered**: a `bool is_done` (can't tell skipped from planned, so a reopened page couldn't restore the plan); `smallint` codes (unreadable in the export and in psql); a Postgres enum type (migrations to change it are awkward in EF Core). Rejected.

## R3 — Which set states each page state allows

**Decision**: The server enforces one table, in one helper used by every set write route:

| Page state | Allowed | Status when omitted |
| --- | --- | --- |
| prepared | planned | planned |
| in progress | planned, done | done |
| finished | done, skipped | done |

Transitions move sets in the same transaction as the page change:

- **Start** (prepared → in progress): sets stay planned.
- **Finish** (`PATCH endedAt` to a value): every planned set becomes skipped (FR-010).
- **Reopen** (`PATCH endedAt: null`): every skipped set becomes planned again, so the reopened session can tick them off. **owner: accepted 2026-10-15**

A status the page state doesn't allow is refused with 400 `invalid_set_status`, never coerced.

**Rationale**: A done set on a page nobody started, or a planned set on a finished page, would have no meaning, so the rules are server-side, not left to the editor. Omitted-status defaults keep `PUT /workouts/{id}/exercises` and the per-set routes backward compatible.

## R4 — "Counts as training" means `status = 'done'`

**Decision**: Every read that feeds history or progress filters `status = 'done'`:

- `GET /exercises/{id}/history`: the best e1RM or best reps per session.
- `GET /exercises` `lastSet` and `firstSet`: the "last time" hints.
- `GET /exercises` `sessionCount`: the distinct workouts with at least one **done** set of that exercise (today: any block). So an all-skipped page and a prepared page don't count (spec edge case, FR-004).
- `GET /workouts` `setCount`: done sets only; a new `notDoneSetCount` gives planned plus skipped, so the list can say "9 sets · 2 skipped" or "4 of 9 done" (the UI mock decides the wording).

Prepared pages can't hold done sets (R3), so the filter covers them too. A started-only check isn't needed.

**Rationale**: One predicate, one meaning. Existing data is all `done`, so every existing number stays the same (SC-006). That is checked by running the existing tests unchanged.

## R5 — Prepared sessions have their own list and create route

**Decision**:
- `GET /workouts` (sessions, keyset-paged) returns started pages only (`started_at is not null`). Its ordering and cursor don't change. A prepared page's id as `before` is 400, like a foreign cursor.
- `GET /workouts/prepared` lists the caller's prepared pages, newest first by `created_at`, unpaged. A handful at a time is the expected use.
- `POST /workouts/prepared` creates one with the optional heading fields and no date or start.
- `POST /workouts/{id}/start { date, startedAt, expectedRevision? }` starts it.

**Rationale**: A separate list keeps the history list's pagination untouched. A separate start route makes the one-way transition explicit; PATCH would have to special-case "date set on a prepared page". Literal segments don't clash with the existing `/{id:int}` routes.

**Alternatives considered**: `GET /workouts?state=prepared` (mixes two orderings behind one cursor); starting via `PATCH /workouts/{id}` (hides a state change inside a field edit). Rejected.

## R6 — Templates are three new tables mirroring the page structure

**Decision**: `templates (id, user_id, name, normalized_name, created_at)` with unique `(user_id, normalized_name)`; `template_exercises (id, template_id, exercise_id, position)`; `template_sets (id, template_exercise_id, set_number, weight, reps, is_warmup)`. Cascades go template → blocks → sets; `exercise_id` is Restrict, as on `workout_exercises`. Bare foreign keys, no upward navigation (Principle I).

**Rationale**: A template is not a page: no date, no state, no revision, no optional details. Reusing `workouts` with a "template" flag would put a third meaning on every query that R4 just made precise. The shape mirrors the workout tables so the copy code reads like the existing bulk save.

**Name uniqueness** (owner, 2026-10-15): `ExerciseNameNormalizer` normalizes template names too. It's a plain name normalizer despite its name; renaming it is unrelated refactoring and is left alone. A clash is caught as a unique violation on `ix_templates_user_id_normalized_name` with the existing `IsUniqueViolation` helper and answered with 409 `template_name_taken`, never merged.

**Alternatives considered**: a JSON column holding the template's exercises (no Restrict FK, so an exercise rename or merge couldn't follow it, and exercise ids inside JSON would dangle). Rejected.

## R7 — Exercise rename and merge carry through to templates

**Decision**: `PATCH /exercises/{id}` rename-merge re-points `template_exercises` as it does `workout_exercises`, in the same `SaveChanges`.

**Rationale**: Restrict on `template_exercises.exercise_id` would otherwise make every merge of an exercise used in a template fail. There is no exercise delete route, so the spec's "deletion refused" edge case is already met by the Restrict FK.

## R8 — Templates are copied, never linked

**Decision**: `POST /templates/{id}/prepare` creates a prepared page with the template's blocks and sets, all planned. Its title is the template name, unless the privacy feature is on and the account lacks optional-details consent, in which case it gets no title. `POST /workouts/{id}/template { name }` creates a template from any page the caller owns: every set's weight, reps and warm-up flag, whatever its status. No column links a page to the template it came from.

**Rationale**: FR-017: later changes to either side never affect the other, and deleting a template leaves its pages alone. Nothing needs a link.

## R9 — No revision on templates

**Decision**: Templates are edited at home with an explicit save; `PUT /templates/{id}` replaces the whole template, last write wins. **owner: accepted 2026-10-15**

**Rationale**: Templates are never autosaved and are rarely edited on two devices at once. Adding the revision machinery is cheap to do later (one column) if it turns out to matter.

## R10 — Export format version 2

**Decision**: The export moves to `formatVersion: 2`:
- `workouts[].date` and `startedAt` may be null (prepared pages).
- `sets[].status` is added.
- New arrays `templates`, `templateExercises` and `templateSets` are added.
- `ExportFieldGuide.cs` explains each new field; its existing test fails otherwise.

Restore accepts 1 and 2. A version 1 file has no status, so its sets restore as `done`, and it has no templates.

**Rationale**: Unlike spec 004's additive `lastBackupAt`, nullable `startedAt` and a set status change what existing fields mean. A reader of version 1 could take a skipped set for training. Bumping the version is the honest signal. The browser CSV conversion (`backupCsv.ts`) gains the status column and the template tables.

## R11 — Restore: prepared pages are always added, templates match by name

**Decision**:
- **Started pages**: deduplicated by `startedAt` as today.
- **Prepared pages** have no `startedAt`, so they're always added and counted in a new `preparedPagesAdded`. Restoring the same file twice gives duplicate plans, which the user deletes. Duplicate plans never reach charts or history. **owner: accepted 2026-10-15**
- **Templates**: matched by normalized name. An existing name keeps the account's template and is counted in `templatesAlreadyPresent`; otherwise it's added and counted in `templatesAdded`.
- **Validation**:
  - A page with a null `startedAt` must also have a null `date` and a null `endedAt`.
  - A set's status must fit its page's state (R3).
  - Template names must be unique within the file after normalizing.
  - Template block exercise references must resolve, or the file is `broken_reference`.
  - Weight, reps, uniqueness and id rules match the page tables.
- **Optional details**: dropped from prepared pages without consent, as for started ones.

**Alternatives considered**: skipping prepared pages on restore, which loses plans when moving to a fresh account, the main reason to restore; deduplicating them by `createdAt`, which restore deliberately never reads. Rejected.

## R12 — Planned sets need complete values

**Decision**: A planned set needs reps, and weight under the same bodyweight rules as a logged set. `reps` stays NOT NULL. In the editor, an incomplete planned row behaves like an incomplete row today: it stays in the draft, and **Save plan** points at it ("Bench Press set 3 needs reps").

**Rationale**: Making `reps` nullable would ripple through every metric and export reader. The spec's edge case said an incomplete planned set "can be kept in the plan". This narrows it to "kept in the draft until complete", and the spec's edge case is amended to match. **owner: accepted 2026-10-15**

## R13 — Optional-details consent and templates

**Decision**: Template names are not optional details. Withdrawing consent doesn't touch them, as it doesn't touch exercise names. Prepared pages are `workouts` rows, so `OptionalDetails.HoldsDetail` and the withdrawal update already cover their title, location, notes and bodyweight, and `POST /workouts/prepared` runs the same consent check as `POST /workouts`.

**Rationale**: A template name is a routine label like "Push A", the same kind of free text as an exercise name, which the consent never covered. This is recorded in the spec's Assumptions; it is listed here so review of Principle V sees it.

## R14 — New rows during a session start done

**Decision (proposed for the UI mock)**: On an in-progress page, a row the user adds, by "Add set" or a new exercise, starts **done**, as every row does today (FR-011). Only rows written while preparing start planned. Tapping a done row's toggle makes it planned again. **owner: accepted 2026-10-15**

**Rationale**: One rule for every in-progress page, whether or not it was prepared, and no extra tap for anyone who logs as they go. The alternative, rows added to a prepared page starting planned, would make the same "Add set" button behave differently depending on how the page began.
