# Data Model: Privacy and Account Lifecycle

Draft, 2026-09-24. Proposed additions require owner review. Existing schema evidence: `backend/GymNotebook.Api/User.cs`, `Exercise.cs`, `Workout.cs`, `WorkoutExercise.cs`, `SetEntry.cs` and `Data/AppDbContext.cs`.

## Existing notebook entities

| Entity | Fields retained | Ownership/relationships |
| --- | --- | --- |
| User | Id int PK, Username unique, PasswordHash, TokenVersion, CreatedAt instant | Root account |
| Exercise | Id, UserId, Name, NormalizedName, IsBodyweight, CreatedAt | Bare UserId FK; unique (UserId, NormalizedName) |
| Workout | Id, UserId, Date, StartedAt, EndedAt?, Title?, Location?, Notes?, BodyweightKg?, CreatedAt | Bare UserId FK; Date is an independent local date |
| WorkoutExercise | Id, WorkoutId, ExerciseId, Position | Workout cascade; Exercise restrict; repeated ExerciseId blocks allowed |
| SetEntry | Id, WorkoutExerciseId, SetNumber, Weight?, Reps, IsWarmup | Block cascade; weight numeric(6,2) kg |

Keep existing field validation, normalization and ordering conventions. BodyweightKg remains numeric(5,2); it does not rewrite historical exercise classifications or progress rules. Free text and non-ASCII values must survive export exactly as stored. Export omits derived NormalizedName and credentials/revocation values; retained user-entered Name is included.

## User additions

| Field | Type / constraint | Purpose |
| --- | --- | --- |
| PrivacyAccountId | UUID, non-null, unique, immutable, server generated | Non-reusable suppression identity independent of username and integer sequence |
| AcknowledgedPrivacyNoticeVersion | Nullable bounded string, proposed max 64 | Latest current version acknowledged by Continue |
| PrivacyNoticeAcknowledgedAt | Nullable timestamptz | When that acknowledgement was committed |

Both acknowledgement fields are null or both populated. Acknowledgement stores no consent flag. Accept only the current published version from trusted notice configuration, never arbitrary client text. Same-version retries preserve the existing timestamp. A new version replaces the latest pair; prior public notice wording remains in versioned operator artifacts, not personal history.

Existing accounts receive UUIDs and null acknowledgement. New registration behaves as before, creating UUID/null fields server-side. Notice acknowledgement is included in export and deleted with User. PrivacyAccountId is exported as account identity, not as a credential.

## PrivacyNoticeVersion — repository artifact, not EF entity

Fields: version (unique, immutable), effectiveAt (UTC instant), publishedAt, materialChangeSummary, full reviewed sections, controller/contact/authority, owner, review date, review evidence reference. A current-version pointer selects the active notice; optional announced successor identifies a future effectiveAt. Prior wording remains operator-accessible in version control.

State: draft → reviewed/announced → effective/current → superseded. Only reviewed content can be published. A pending notice is communicated before activation; acknowledging the current notice never authorizes new processing. Version identifiers are compared by equality, not lexicographic date ordering. No placeholders can reach publication.

## DeletionReceipt — short-lived transactional entity

Fields: PrivacyAccountId (UUID unique/key, no FK to deleted User), DeletionBoundaryAt (timestamptz), SuppressionExpiresAt (timestamptz). It contains no integer user ID, username, password/token, notebook content or IP address.

Insert in the same transaction as active deletion. The boundary timestamp is taken after password/confirmation and exclusive coordination, immediately before preparing the independent receipt. It is a conservative retention origin preceding commit, so deadlines cannot be extended by slow commit/finalization. Transaction/lock timeouts bound this interval. Success is still defined by completed commit, not this timestamp.

Local receipt state: absent → committed with active deletion → externally finalized → removed. A rolled-back transaction leaves no receipt and the pre-deletion notebook intact. Remove local receipt promptly after archival and always before boundary +24 hours. Its backups must also be unusable before boundary +31 days; the receipt table is not an indefinite audit trail.

## Independent deletion ledger — restricted operational storage

Same minimal payload as DeletionReceipt; protocol metadata distinguishes PREPARED and COMMITTED. Conditional/idempotent writes key by immutable account UUID and operation boundary. Prepared records may concern an account whose transaction never committed, so they cannot automatically suppress that account.

State: absent → durably PREPARED → COMMITTED after authoritative DB commit evidence → expired/disposed. Proven rollback removes PREPARED. Unknown outcome remains unresolved and blocks affected restore. Never infer rollback from absence of a receipt in an old backup. A restored database is not authoritative evidence for post-backup commits.

Absolute expiry is no later than boundary +31 calendar days, including storage versions, snapshots and copied evidence. Failed/prepared operations are bounded from their original preparation time too. If ambiguity remains, eliminate affected restore sources before removing evidence. Reusing a username creates a different UUID; replay cannot remove that new account.

PREPARED may temporarily be linked to a live account. Before export, reconcile and remove any such retained preparation; if that cannot be verified, fail export safely with the contact path rather than silently omit a user-linked feature record. This is checked under export's short initialization guard before establishing its snapshot.

## Export document — transient value, not stored entity

See [API contract](contracts/api.md) for the full schema. One JSON object includes formatVersion, snapshotAt, fieldGuide, account, exercises, workouts, workoutExercises, sets and privacyRecords. Empty collections remain explicit arrays; absent acknowledgement is null. No completed export or export-history row is persisted by this design.

Streams/buffers are request-owned and released on completion, cancellation, expiry or observed deletion. No disk spool or service-side URL is permitted by the initial design. If implementation requires persistence, revisit the design and prove deletion-time cleanup and the 24-hour maximum before enabling it.

## Reviewed operational records

These are maintained records, not new public administration APIs or EF entities. Public non-personal policy belongs in `docs/privacy/`; request correspondence, deletion evidence, credentials and private provider evidence do not belong in Git.

| Record | Required fields / validation |
| --- | --- |
| Processing decision | Purpose, categories, necessity, lawful basis/rationale, health-data assessment/additional condition, consent conclusion, reviewer/date, evidence, unresolved findings |
| Supplier inventory | Provider, processor/subprocessor/other role, purpose/categories, processing locations, agreement/transfer evidence, subprocessors, retention/deletion assistance, owner/review date/status |
| Retention rule | Category/copy type, purpose, original start event, maximum duration, disposal mechanism, owner, evidence, exception status |
| Rights request | Opaque request reference, request type, receipt date, identity-verification outcome, original one-calendar-month deadline, response/extension/refusal reason, closure, absolute disposal deadline |

The rights register's scope and numeric retention period need purpose-specific owner review before collection; no indefinite default or approved exception is invented. Any account-linked feature record introduced beyond the defined acknowledgement must be classified for export/deletion before implementation. Broader correspondence/log access uses the reviewed contact process; exclusions require review, not automatic denial.

## Transactions and migration validation

1. Backfill UUIDs with uniqueness enforced; keep existing keys and relationships unchanged. Never seed notice acknowledgement or consent. Ensure old backups predating this migration are retired before activation or handled by a reviewed compatible restore migration; do not generate a new UUID on restore and assume it matches old deletion evidence.
2. Preserve all existing constraints/cascades. Review the generated migration for unrelated changes before applying it to real Postgres tests.
3. Account operations take shared lifecycle access and freshly validate; deletion/password change take exclusive access. Reuse the existing transaction boundary in bulk writes. Account lock order precedes notebook-row operations.
4. Deletion removes workouts/blocks/sets before exercises, then User/acknowledgement, while inserting the receipt atomically. Rollback restores all active data, not an empty usable account.
5. Restore uses committed independent evidence and authoritative coverage proof, rejects unresolved prepared records, applies suppression before ingress, rotates JWT signing credentials, and verifies original retention deadlines. An expired evidence record cannot legitimize an over-age restore source.
