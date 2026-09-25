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
| OptionalDetailsConsentVersion | Nullable bounded string, max 64; amendment 2026-09-25 (FR-029–FR-035) | Consent statement version the account consented to for optional workout details |
| OptionalDetailsConsentedAt | Nullable timestamptz | When that consent was committed |
| SignInSuspendedAt | Nullable timestamptz; approved 2026-09-24 (P3) | Set only by the restore fallback when a deletion's outcome is unknown (R6 Q2c); the timestamp records when. Login verifies the password first, then returns 403 `account_suspended`; `OnTokenValidated` also rejects it as a backstop, at no extra query |

Both acknowledgement fields are null or both populated. Acknowledgement stores no consent flag. Accept only the current published version from trusted notice configuration, never arbitrary client text. Same-version retries preserve the existing timestamp. A new version replaces the latest pair; prior public notice wording remains in versioned operator artifacts, not personal history.

Existing accounts receive UUIDs and null acknowledgement. New registration behaves as before, creating UUID/null fields server-side. Notice acknowledgement is included in export and deleted with User. PrivacyAccountId is exported as account identity, not as a credential. SignInSuspendedAt is a restricted security control value, excluded from export like TokenVersion. A suspended account cannot sign in to export, and its requests go through the contact path.

The operator sets and clears SignInSuspendedAt by documented manual SQL in `docs/privacy/restore.md`; there is no API or admin screen. To resolve a suspension, the operator confirms the intent with the user through the contact path. If deletion was intended, the operator completes it using the restore runbook's deletion steps. Otherwise the operator clears the marker.

### Optional-details consent (amendment 2026-09-25)

"Optional workout details" are Workout `Title`, `Location`, `Notes` and `BodyweightKg` (spec FR-029). Exercise `Name` is outside the consent.

- **Pair rule:** both consent fields are null, or both are populated, like the acknowledgement pair. Null means no consent. Refusal and withdrawal are not recorded: they leave the pair null.
- **Grant:** accept only the current consent statement version from trusted configuration. A same-version retry keeps the existing timestamp.
- **Withdrawal (FR-033):** one transaction, under the account's shared lifecycle access like other notebook writes. It sets the pair to null and runs `UPDATE workouts SET title = NULL, location = NULL, notes = NULL, bodyweight_kg = NULL WHERE user_id = @id`. It is idempotent: repeating it changes nothing. A concurrent export sees either the state before or after, never a mix.
- **Enforcement (FR-032):** with the feature flag on and the pair null, a workout create or update carrying a non-empty optional detail is rejected before any change. Setting a detail to null or empty is always allowed.
- **Transition pending:** derived, not stored. True when the pair is null and at least one of the account's workouts holds a non-null optional detail. After the transition deadline the operator's clearing step makes this false for every account.
- **Existing accounts:** the migration adds the pair as null. It never seeds consent (spec FR-035).
- **Export and deletion:** exported under `privacyRecords`, and deleted with User.

## Consent statement version — repository artifact, not EF entity

Fields: `version` (unique, immutable), `effectiveAt`, `publishedAt`, the statement `sections` (plain text, like the notice), and operator metadata (`owner`, `reviewDate`, `reviewEvidence`). Proposed location: `docs/privacy/consent/`, with an `index.json` naming the `current` version and every published version. It is embedded and validated at startup the same way as the notices (`PrivacyNoticeCatalog`). This amendment has one version. Changing what the statement covers needs its own specification change (FR-034), so there is no announced-successor mechanism.

## Transition clearing — operator step, not EF entity

At the transition deadline (30 calendar days after the flag is switched on, spec FR-035), the operator runs one documented SQL statement. It clears the optional details of every workout whose owner has no consent pair, and records only the number of accounts and workouts affected, in `docs/privacy/release-checklist.md`. This follows the suspension marker's precedent: operator SQL, no API or admin screen. A follow-up query must then show zero such workouts (SC-008).

## PrivacyNoticeVersion — repository artifact, not EF entity

Fields: version (unique, immutable), effectiveAt (UTC instant), publishedAt, materialChangeSummary, full reviewed sections, controller/contact/authority, owner, review date, review evidence reference. A current-version pointer selects the active notice; optional announced successor identifies a future effectiveAt. Prior wording remains operator-accessible in version control.

State: draft → reviewed/announced → effective/current → superseded. Only reviewed content can be published. A pending notice is communicated before activation; acknowledging the current notice never authorizes new processing. Version identifiers are compared by equality, not lexicographic date ordering. No placeholders can reach publication.

## Deletion log lines — operational log records, not EF entity

Chosen in [research R6](research.md#r6--independent-restore-evidence). **Terminology:** these lines are the spec's "deletion evidence" (FR-020, SC-006). The "restore suppression" in research R5 and the "minimal suppression evidence" in contracts/ui.md mean the same thing: re-deleting an account during restore reconciliation. These are structured lines in the existing Container Apps console logs. They are the fallback restore evidence when the pre-restore branch is unusable.

Fields: event (`deletion.intent`, `deletion.committed` or `deletion.rolled_back`), PrivacyAccountId (UUID) and DeletionBoundaryAt (timestamptz). They contain no integer user ID, username, password/token, notebook content or IP address.

- **intent:** written under exclusive coordination before commit.
- **committed:** written after commit and before the success response.
- **rolled_back:** written when the transaction definitively rolls back, so the fallback can release that intent.

The boundary timestamp is taken after password/confirmation and exclusive coordination, immediately before the intent line. It is a conservative retention origin preceding commit, so a slow commit cannot extend deadlines. Transaction/lock timeouts bound this interval. Success is still defined by completed commit, not this timestamp.

The lines follow the 30-day log retention in R7 and are not an indefinite audit trail. They need the FR-004/FR-019 continued-retention justification (R6 Q2e).

## Restore evidence and invariant

The primary evidence is the preserved pre-restore Neon branch. It is not an entity and never enters this schema.

**Invariant (R6 Q2d):** account deletion is the only operation that removes a User row. A test guards this, and any new path that removes User rows must revisit R6 first. A restored database is not evidence for deletions after its restore point; only the preserved branch or complete log lines are.

## Export document — transient value, not stored entity

See [API contract](contracts/api.md) for the full schema. One JSON object includes formatVersion, snapshotAt, fieldGuide, account, exercises, workouts, workoutExercises, sets and privacyRecords. Empty collections remain explicit arrays; absent acknowledgement or optional-details consent is null. No completed export or export-history row is persisted by this design.

Streams/buffers are request-owned and released on completion, cancellation, expiry or observed deletion. No disk spool or service-side URL is permitted by the initial design. If implementation requires persistence, revisit the design and prove deletion-time cleanup and the 24-hour maximum before enabling it.

## Reviewed operational records

These are maintained records, not new public administration APIs or EF entities. Public non-personal policy belongs in `docs/privacy/`; request correspondence, deletion log evidence, credentials and private provider evidence do not belong in Git.

| Record | Required fields / validation |
| --- | --- |
| Processing decision | Purpose, categories, necessity, lawful basis/rationale, health-data assessment/additional condition, consent conclusion, reviewer/date, evidence, unresolved findings |
| Supplier inventory | Provider, processor/subprocessor/other role, purpose/categories, processing locations, agreement/transfer evidence, subprocessors, retention/deletion assistance, owner/review date/status |
| Retention rule | Category/copy type, purpose, original start event, maximum duration, disposal mechanism, owner, evidence, exception status |
| Rights request | Opaque request reference, request type, receipt date, identity-verification outcome, original one-calendar-month deadline, response/extension/refusal reason, closure, absolute disposal deadline |

The rights register's scope and numeric retention period need purpose-specific owner review before collection; no indefinite default or approved exception is invented. Any account-linked feature record introduced beyond the defined acknowledgement must be classified for export/deletion before implementation. Broader correspondence/log access uses the reviewed contact process; exclusions require review, not automatic denial.

## Transactions and migration validation

1. Backfill UUIDs with uniqueness enforced; keep existing keys and relationships unchanged. Never seed notice acknowledgement or consent. The optional-details consent pair (amendment 2026-09-25) is added as null in its own reviewed migration. Ensure old backups predating this migration are retired before activation or handled by a reviewed compatible restore migration; do not generate a new UUID on restore and assume it matches old deletion evidence.
2. Preserve all existing constraints/cascades. Review the generated migration for unrelated changes before applying it to real Postgres tests.
3. Account operations take shared lifecycle access and freshly validate; deletion/password change take exclusive access. Reuse the existing transaction boundary in bulk writes. Account lock order precedes notebook-row operations.
4. Deletion removes workouts/blocks/sets before exercises, then User/acknowledgement, in one transaction bracketed by the intent and committed log lines. Rollback restores all active data, not an empty usable account, and logs `deletion.rolled_back`.
5. Restore diffs PrivacyAccountId between the restored database and the preserved pre-restore branch, and re-deletes accounts missing from the preserved branch before ingress. If that branch is unusable, it uses gap-free log lines instead, and suspends sign-in for intent-only accounts. It rotates JWT signing credentials and verifies original retention deadlines. Unverifiable evidence keeps access closed.
