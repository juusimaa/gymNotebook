# Data Model: Predefined Sessions

Three migrations, one per backend PR (see [plan.md](plan.md#delivery)). Review each generated migration for unintended changes before applying it (Principle IV).

## Workout (changed): migration `AllowPreparedWorkouts`

| Field | Column | Change |
| --- | --- | --- |
| `Date` | `date` | `date not null` → `date null`. `DateOnly?` in C#. |
| `StartedAt` | `started_at` | `timestamptz not null` → `timestamptz null`. `DateTimeOffset?` in C#. |

New check constraints:

- `ck_workouts_started_pair`: `(date IS NULL) = (started_at IS NULL)`. A page has both or neither.
- `ck_workouts_ended_requires_start`: `ended_at IS NULL OR started_at IS NOT NULL`. A page can't finish before it starts.

**State is derived** (research R1): prepared when `started_at` is null, in progress when `started_at` is set and `ended_at` is null, finished when both are set. A small static `WorkoutState.Of(startedAt, endedAt)` names it in C# so the routes don't repeat the null checks.

**State transitions**:

```
prepared ──start──▶ in progress ──finish──▶ finished
                         ▲                     │
                         └──────reopen─────────┘
```

There is no way back to prepared. A started page can't be "un-started", since its sets may be done.

Existing rows all have both columns set, so they satisfy both constraints and no data step is needed.

## SetEntry (changed): migration `AddSetStatus`

| Field | Column | Type | Notes |
| --- | --- | --- | --- |
| `Status` | `status` | `text not null default 'done'` | `SetStatus` enum (`Planned`, `Done`, `Skipped`). A value converter maps it to and from `planned`/`done`/`skipped`. |

- Check constraint `ck_set_entries_status`: `status IN ('planned', 'done', 'skipped')`.
- Existing rows take the default and become `done` (FR-012).
- Which statuses a page state allows, and what start, finish and reopen do to them, is research R3. That rule spans two tables, so the API enforces it; the database doesn't.

## Template (new): migration `AddTemplates`

```
Template          (id, user_id, name, normalized_name, created_at)
TemplateExercise  (id, template_id, exercise_id, position)
TemplateSet       (id, template_exercise_id, set_number, weight?, reps, is_warmup)
```

| Table | Constraints |
| --- | --- |
| `templates` | FK `user_id` → `users`, cascade (as `workouts`). **Unique `ix_templates_user_id_normalized_name` on `(user_id, normalized_name)`.** `created_at` default `now()`. |
| `template_exercises` | FK `template_id` → `templates`, cascade. FK `exercise_id` → `exercises`, **restrict**, as on `workout_exercises`. Index on `template_id`. |
| `template_sets` | FK `template_exercise_id` → `template_exercises`, cascade. `weight numeric(6,2)`. Index on `(template_exercise_id, set_number)`. |

- `Name` keeps the casing the user typed. `NormalizedName` comes from `ExerciseNameNormalizer.Normalize` (trimmed, lowercased, internal whitespace collapsed), as for exercises (owner, 2026-10-15).
- A blank name is 400. A clash is 409 `template_name_taken` (research R6).
- Templates have no status, no revision and no optional-details fields.
- Navigation goes down only (`Template.Exercises`, `TemplateExercise.Sets`), like `Workout.WorkoutExercises`. There are no upward navigation properties (Principle I).

## Changed behaviour on existing tables

- **Account deletion** (`AccountDeletion.cs`): delete `templates` for the user before `exercises`, since the Restrict FK from `template_exercises` would otherwise block the exercise delete. Blocks and sets go by cascade. Prepared pages are `workouts` rows and are already deleted.
- **Exercise rename-merge** (`PATCH /exercises/{id}`): re-point `template_exercises` from the losing exercise as well as `workout_exercises` (research R7).
- **Optional-details withdrawal**: no change. It already updates every `workouts` row of the user, prepared ones included.

## Export format version 2 (changed)

| Field | Change |
| --- | --- |
| `formatVersion` | `1` → `2` |
| `workouts[].date`, `workouts[].startedAt` | may be `null` (prepared page) |
| `sets[].status` | new: `"planned" \| "done" \| "skipped"` |
| `templates[]` | new: `id`, `userId`, `name`, `createdAt` |
| `templateExercises[]` | new: `id`, `templateId`, `exerciseId`, `position` |
| `templateSets[]` | new: `id`, `templateExerciseId`, `setNumber`, `weight`, `reps`, `isWarmup` |

`ExportFieldGuide.cs` gains an entry for each new field. Restore reads versions 1 and 2 (research R10, R11). The CSV conversion in `frontend/src/screens/backupCsv.ts` adds a `status` column to the sets file and three template files.

## Restore writes (changed)

| Table | From the file | Set by the server |
| --- | --- | --- |
| `workouts` (prepared) | as for started pages, with `date`/`startedAt`/`endedAt` null | always added (R11) |
| `sets` | adds `status` (v2). A v1 file restores as `done` | |
| `templates` | `name` | `Id`, `UserId`, `NormalizedName`, `CreatedAt` = restore time. Skipped when the name already exists for the account |
| `template_exercises` | `position`, exercise via the file's `exerciseId` → matched or created exercise | `Id`, `TemplateId` |
| `template_sets` | `setNumber`, `weight`, `reps`, `isWarmup` | `Id`, `TemplateExerciseId` |

**Restore result** gains `preparedPagesAdded`, `templatesAdded` and `templatesAlreadyPresent` (integers).
