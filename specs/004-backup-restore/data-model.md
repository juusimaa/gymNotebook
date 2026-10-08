# Data Model: Backup and Restore

## User (changed)

| Field | Column | Type | Notes |
| --- | --- | --- | --- |
| `LastBackupAt` | `last_backup_at` | `timestamptz null` | Set when a JSON backup has finished sending (plan D6). Null until the first one. Removed with the `User` row on account deletion, so deletion needs no new code; the deletion tests assert it's gone. |

Migration: `AddUserLastBackupAt` adds the nullable column; nothing else. Review the generated migration for unintended changes before applying it.

This is one timestamp, not a history: each backup overwrites it. It amends spec 001's "No export record/history is persisted" and needs the processing decision (P1, account administration) to list it before release.

## Restore writes (no schema change)

Restore inserts rows into existing tables, with new ids and the caller's `UserId`:

| Table | From the file | Set by the server |
| --- | --- | --- |
| `exercises` | `name`, `isBodyweight` (only for unmatched names used by an added page) | `Id`, `UserId`, `NormalizedName` (existing normalizer), `CreatedAt` = restore time |
| `workouts` | `date`, `startedAt`, `endedAt`, `title`, `location`, `notes`, `bodyweightKg` (the last four dropped without consent, FR-017) | `Id`, `UserId`, `CreatedAt` = restore time, `Revision` = 1 if spec 003 has landed |
| `workout_exercises` | `position`, and the exercise via the file's `exerciseId` → matched or created exercise | `Id`, `WorkoutId` |
| `sets` | `setNumber`, `weight`, `reps`, `isWarmup` | `Id`, `WorkoutExerciseId` |

Never read from the file: any `id` as a database key, `userId`, `createdAt`, `account`, `privacyRecords`, `fieldGuide`.

No new index. The "already there" check (`user_id = @user AND started_at = ANY(@instants)`) uses the existing index on `workouts.user_id`; a notebook's page count keeps that cheap. If the fixture shows otherwise, an index on `(user_id, started_at)` is a follow-up with its own migration.

## Export format version 1 (changed, additive)

| Field | Change |
| --- | --- |
| `account.lastBackupAt` | New: instant or null. The value **before** this export (the stamp is written after the file). Explained in `ExportFieldGuide.cs`; the existing test fails if it isn't. |

The format version stays 1: the change only adds a field, and restore ignores `account`. 001's export contract is updated in the same PR.

## Restore result (response only, not stored)

| Field | Type |
| --- | --- |
| `pagesAdded` | integer |
| `setsAdded` | integer |
| `pagesAlreadyPresent` | integer |
| `exercisesCreated` | string[] (names, as written in the file) |
| `classificationKept` | string[] (names of matched exercises whose Bodyweight/Loaded differed) |
| `optionalDetailsDropped` | integer (pages that lost at least one detail) |
