# API Contract: Predefined Sessions

All routes require authentication and the account lifecycle filter, like the existing `/workouts` and `/exercises` groups. A missing or foreign id is 404, never 403. Errors use the existing `ErrorResponse { code }` body.

> **Revised (research R15, 2026-10-15).** `POST /templates/{id}/start` replaces `POST /templates/{id}/prepare` (PR 9). Prepared pages are then removed (PR 10): `GET/POST /workouts/prepared`, `POST /workouts/{id}/start`, the `prepared` page state, its row in the status table and the `not_started` refusal go; `date` and `startedAt` are never null again, and a `before` cursor no longer has a prepared page to refuse. The sections below that describe prepared pages are kept for the record and marked as removed.

## Set status on existing routes

`SetEntryResponse` gains `status: "planned" | "done" | "skipped"`.

These inputs gain an optional `status`:

- `PutSetInput` (in `PUT /workouts/{id}/exercises`)
- `CreateSetRequest` (`POST /workouts/{id}/sets`)
- `UpdateSetRequest` (`PATCH /workouts/{id}/sets/{setId}`)

When `status` is omitted it defaults to `planned` on a prepared page and `done` otherwise. A `PATCH` without it keeps the set's current status. A status the page's state doesn't allow is 400 `invalid_set_status` (research R3):

| Page state | Allowed |
| --- | --- |
| prepared | planned |
| in progress | planned, done |
| finished | done, skipped |

`WorkoutDetailResponse.date` and `startedAt` become nullable (null on a prepared page).

## Changed: `GET /workouts`

- Returns started pages only. A prepared page's id as `before` is 400, as for a foreign cursor.
- `setCount` counts done sets.
- New field `notDoneSetCount` counts planned plus skipped sets.

## Changed: `PATCH /workouts/{id}`

| Request on a… | Result |
| --- | --- |
| prepared page, with `date`, `startedAt` or `endedAt` | 400 `not_started`. Use `/start`. |
| in-progress page, `endedAt` set (finish) | every planned set becomes skipped in the same transaction |
| finished page, `endedAt: null` (reopen) | every skipped set becomes planned |
| prepared page, heading fields only (title, location, notes, bodyweight) | allowed as today, with the consent check |

## Changed: `GET /exercises` and `GET /exercises/{id}/history`

- `lastSet`, `firstSet` and the history points read done sets only.
- `sessionCount` counts workouts with at least one done set of the exercise.

## Changed: `PATCH /exercises/{id}`

A rename onto an existing name also re-points that exercise's template blocks.

## Removed in PR 10 (R15): prepared pages

### `GET /workouts/prepared`

200: `PreparedWorkoutSummaryResponse[]`, newest `createdAt` first, unpaged:

```json
[{ "id": 41, "title": "Push A", "createdAt": "2026-10-15T18:02:11Z",
   "exerciseNames": ["Bench Press", "Overhead Press"], "exerciseCount": 2, "setCount": 7 }]
```

`title` follows the consent rule of the session list.

### `POST /workouts/prepared`

```json
{ "title": "Push A", "bodyweightKg": null, "location": null, "notes": null }
```

- 201: `WorkoutDetailResponse` with `date: null`, `startedAt: null`, `endedAt: null`, `revision: 1` and no exercises.
- 403: `optional_details_consent_required`, as `POST /workouts`.

Blocks and sets are then saved with `PUT /workouts/{id}/exercises`, as today.

### `POST /workouts/{id}/start`

```json
{ "date": "2026-10-16", "startedAt": "2026-10-16T07:12:00+03:00", "expectedRevision": 3 }
```

- 200: `WorkoutDetailResponse`, now in progress, with its revision bumped. Sets stay planned.
- 400: `invalid_request` when date or startedAt is missing.
- 404: not found, or not the caller's.
- 409: `already_started` when the page isn't prepared. 409 `page_changed` on a stale `expectedRevision`.

## New: templates

### `GET /templates`

200: `TemplateSummaryResponse[]`, ordered by name:

```json
[{ "id": 3, "name": "Push A", "exerciseNames": ["Bench Press", "Overhead Press"], "exerciseCount": 2, "setCount": 7 }]
```

### `GET /templates/{id}`

200: `TemplateDetailResponse`:

```json
{ "id": 3, "name": "Push A",
  "exercises": [{ "id": 9, "exerciseId": 12, "exerciseName": "Bench Press", "isBodyweight": false,
                  "sets": [{ "id": 31, "setNumber": 1, "weight": 60, "reps": 8, "isWarmup": true }] }] }
```

### `POST /templates` and `PUT /templates/{id}`

```json
{ "name": "Push A",
  "exercises": [{ "exerciseName": "Bench Press", "sets": [{ "weight": 60, "reps": 8, "isWarmup": true }] }] }
```

- 201 (POST) or 200 (PUT): `TemplateDetailResponse`.
- PUT replaces the whole template in one transaction, like `PUT /workouts/{id}/exercises`. Last write wins (research R9).
- Exercise names are get-or-create, as on pages, so a new exercise arrives by being typed.
- 400: `invalid_request` for a blank name, a blank exercise name or reps < 1.
- 409: `template_name_taken` when the normalized name belongs to another of the caller's templates. Nothing is written.

### `DELETE /templates/{id}`

204. Pages started from it are untouched.

### `POST /templates/{id}/start` (R15, replaces `POST /templates/{id}/prepare`)

```json
{ "date": "2026-10-16", "startedAt": "2026-10-16T07:12:00+03:00" }
```

- 201: `WorkoutDetailResponse` for a new in-progress page, dated and started as sent (`startedAt` stored in UTC), holding a copy of the template's blocks and sets, all `planned`. `Location` is `/workouts/{id}`.
- `title` is the template name, or null when the privacy feature is on and the account lacks optional-details consent.
- 400: `invalid_request` when `date` or `startedAt` is missing.
- 404: template not found or not the caller's.

### `POST /workouts/{id}/template`

```json
{ "name": "Push A" }
```

- 201: `TemplateDetailResponse` copying every block and set of the page (weight, reps, warm-up), whatever its status.
- 404: page not found or not the caller's.
- 409: `template_name_taken`.

## Changed: backup

- `POST /account/export` writes `formatVersion: 2` ([data-model.md](../data-model.md#export-format-version-2-changed)).
- `POST /account/restore` accepts versions 1 and 2. Its result gains `templatesAdded` and `templatesAlreadyPresent`. *(R15: `preparedPagesAdded` is gone; an older file's prepared pages become templates and count in `templatesAdded`. The export writes no prepared pages.)*
- New `backup_invalid` reasons:
  - a prepared page with a date, start or end
  - a set status its page's state doesn't allow
  - duplicate template names in the file
