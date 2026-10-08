# API Contract: Backup and Restore

All three routes are mapped whether or not `PRIVACY_LIFECYCLE_ENABLED` is on (plan D2). They run under the existing lifecycle coordination: a deleted account or revoked token gets 401, a timed-out lock wait 503 `temporarily_unavailable`. Every response carries `Cache-Control: no-store`. Request bodies, file contents and passwords are never logged or echoed.

## `POST /account/export` (changed)

Spec 001's export, now mapped always.

- **Request**: `{ "currentPassword": "…", "format": "json" | "csv" }`. `format` is optional and defaults to `json`. Any other value: 400 `invalid_request`.
- **Response**: unchanged. The same JSON stream for both formats; the browser writes the CSV (plan D4).
- **After the last byte**, when `format` is `json`: `users.last_backup_at` is set to the current time on a separate short write under a delivery guard (plan D6). A failure there is logged without personal data and doesn't affect the response.
- **File**: gains `account.lastBackupAt` (data-model.md).
- **Errors**: unchanged (400 `password_verification_failed`, 429 `export_in_progress`, 503, 401).

## `GET /account/backup` (new)

- **Auth**: bearer.
- **200**: `{ "lastBackupAt": "2026-10-03T06:42:11Z" }`, or `null`.

## `POST /account/restore` (new)

Adds the pages from a backup file that the notebook doesn't have.

- **Auth**: bearer. No password (plan D8; owner, Q2).
- **Request**: `Content-Type: application/json`; the body is the backup file exactly as downloaded. Larger than `Restore:MaxBytes` (default 25 MB): **413** before the body is read. Not JSON: **415**.
- **200**:
  ```json
  {
    "pagesAdded": 2,
    "setsAdded": 48,
    "pagesAlreadyPresent": 212,
    "exercisesCreated": ["Sääriprässi"],
    "classificationKept": ["Pull-up"],
    "optionalDetailsDropped": 0
  }
  ```
  Everything reported is committed; nothing else is written.
- **400** `{ "code": "backup_invalid", "reason": "…" }`, nothing written. `reason` is one of:
  - `not_a_backup`: not an object with `formatVersion`, `exercises`, `workouts`, `workoutExercises` and `sets`.
  - `unsupported_version`: `formatVersion` isn't 1.
  - `broken_reference`: a block names a missing page or exercise, or a set a missing block.
  - `invalid_value`: a value breaks a rule of the existing routes (a negative weight, reps out of range, an empty exercise name, an end before its start, a duplicate position in a page…).
  No position, id or value from the file is returned.
- **429** `{ "code": "restore_in_progress" }`: another restore of this account is running. **429** with Retry-After: the per-account rate limit (5 per 10 minutes).
- **503** `temporarily_unavailable`: rolled back; safe to retry.

## CSV columns

Built in the browser by `toCsv` (plan D4, D5). One header row, then one row per set: pages in `date`, `startedAt` order, blocks by position, sets by set number. A page with no blocks, or a block with no sets, gets one row with the later columns empty.

| Header | Value | Example (fi) |
| --- | --- | --- |
| `Date` | `date`, as stored | `2026-10-03` |
| `Start` | `startedAt` in the browser's time zone, `HH:mm` | `09:34` |
| `End` | `endedAt` likewise; empty when unfinished | `10:51` |
| `Title` | text or empty | `Push A` |
| `Gym` | `location` | `Kamppi` |
| `Bodyweight (kg)` | `bodyweightKg` | `82,4` |
| `Notes` | text, quoted when needed | `"Hartia jumissa; kevyt päivä"` |
| `Block` | position, 1-based as shown in the app | `2` |
| `Exercise` | name | `Sääriprässi` |
| `Bodyweight exercise` | `yes` / `no` | `no` |
| `Set` | set number | `3` |
| `Warm-up` | `yes` / `no` | `no` |
| `Weight (kg)` | `weight`; added load on a bodyweight exercise; empty when null, never `0` | `142,5` |
| `Reps` | integer | `8` |

Decimals keep their stored precision, without display rounding. The file starts with a UTF-8 BOM and uses CRLF. Comma-decimal locales use `;` between fields; others `,`. Fields containing the separator, `"`, CR or LF are quoted, with `"` doubled. Text cells starting with `=`, `+`, `-`, `@`, tab or CR get a leading `'`.
