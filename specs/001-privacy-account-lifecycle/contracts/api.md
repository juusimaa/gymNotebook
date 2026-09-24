# HTTP and Export Contract

Draft for owner review, 2026-09-24. These routes are proposed, not implemented. Existing auth and notebook endpoints remain compatible except for lifecycle coordination required by the spec.

## Common behavior

- HTTPS in production; existing bearer JWT, CORS allow-list and invite gate remain. Ownership derives exclusively from the validated caller. New account operations accept no user ID/username target.
- Personal responses and errors use `Cache-Control: no-store`; do not log bodies, passwords, authorization headers or export contents. Public notice content contains no personal account data.
- Fresh validation under lifecycle coordination checks current account existence, token version and expiry. Invalid/missing/revoked/expired bearer: 401 with no personal data. New operation wrong-current-password: 400 `{ "code": "password_verification_failed" }`. Existing login/change-password semantics remain unchanged.
- Invalid input: 400 `invalid_request`; current-notice mismatch: 409 `notice_version_changed`; rate limit/concurrent-export limit: 429 with Retry-After when available; recoverable precommit outage: 503 `temporarily_unavailable`; uncertain deletion commit/finalization: 503 `deletion_outcome_unknown`. Never expose exception details.
- Unknown outcome instructs contact/revalidation; it does not claim the notebook is intact. Known precommit rollback leaves it intact. A later 401 cannot prove which outcome occurred.
- Current password is verified anew for each export/deletion. Apply existing per-IP auth policy plus reviewed per-account throttling; proposed 10 attempts per 60 seconds, no queue. Only one export stream per account at a time; rejected concurrent exports disclose no data and do not modify the notebook.
- Specific missing/unowned notebook resources remain 404; foreign/nonexistent pagination cursors remain identical 400s. The notice gate is UI routing, not an ownership or consent authorization rule.

## Endpoints

| Method/path | Request | Success | Additional behavior |
| --- | --- | --- | --- |
| GET /privacy/notice | Public, no body | 200 current notice document | No acknowledgement side effect; no account data; optional announced successor metadata |
| GET /account/privacy | Bearer, no body | 200 account privacy state | Current notice version, latest acknowledgement, whether acknowledgement is needed |
| PUT /account/privacy/acknowledgement | Bearer; `{ "noticeVersion": "…" }` | 200 updated acknowledgement | Current version only; same version idempotent; no consent flag |
| POST /account/export | Bearer; `{ "currentPassword": "…" }` | 200 JSON attachment stream | Fresh password; stable snapshot; cancellation on invalidation; never 202/public link |
| POST /account/delete | Bearer; `{ "currentPassword": "…", "confirmDeletion": true }` | 200 deletion receipt response | Missing/false confirmation fails before mutation; password plus explicit user action |

POST for deletion avoids relying on DELETE request-body handling and allows password plus explicit confirmation. Both sensitive actions require JSON; reject non-JSON with 415 and malformed JSON with 400. Password strings are not trimmed/transformed. Do not auto-retry deletion or export with retained passwords.

### Notice document

Fields: `version` string, `effectiveAt` UTC instant, `publishedAt` UTC instant, `materialChangeSummary` string, `sections` array of `{ id, heading, paragraphs }`, and `announcedSuccessor` null or `{ version, effectiveAt, materialChangeSummary, sections }`. Reviewed sections contain every FR-002 disclosure and FR-025 rights/contact instruction. UI renders safe text/structured links, never unchecked HTML. Archive prior wording in repository artifacts.

### Account privacy state

Fields: `currentNoticeVersion` string, `acknowledgement` null or `{ noticeVersion, acknowledgedAt }`, and `requiresAcknowledgement` boolean determined on the server. No password hash, token version or other revocation values. Acknowledgement response has the same acknowledgement shape; success means the version was acknowledged through Continue, not read or consented to. Public notice rendering never writes it.

### Deletion response

Return only after database removal, independent ledger finalization and cancellation boundary succeed. Body: `status: "deleted"`, `retentionBoundaryAt` (the conservative precommit retention origin, not the actual commit time), `backupsExpireBy` (no later than boundary +30 calendar days), `deletionEvidenceExpiresBy` (no later than boundary +31 days), and `logRetentionNotice` explaining reviewed restricted logs expire within 30 days of their original collection. No per-log personal data or new session token. This minimal terminal outcome is authorized by the completed operation and does not reauthenticate the now-deleted User.

If the response is lost, old-session retry receives 401 and the client says the session no longer authorizes access. It must not reconstruct this success response locally. Already delivered user files are outside service control.

## Export format version 1

Headers: `Content-Type: application/json; charset=utf-8`, `Content-Disposition: attachment; filename="gym-notebook-export.json"`, `Cache-Control: no-store`. Filename has no username. Frontend can use this fixed filename without additional CORS header exposure. No range/resume URL in version 1; retries produce a fresh snapshot.

| Top-level field | Type / content |
| --- | --- |
| formatVersion | Integer 1 |
| snapshotAt | UTC ISO-8601 instant captured with the first snapshot query |
| fieldGuide | Object documenting every field below, units, nulls, keys/relationships, ordering and date interpretation |
| account | `{ id: integer, privacyAccountId: UUID string, username: string, createdAt: instant }` |
| exercises | Array of `{ id, userId, name, isBodyweight, createdAt }` |
| workouts | Array of `{ id, userId, date, startedAt, endedAt, title, location, notes, bodyweightKg, createdAt }` |
| workoutExercises | Array of `{ id, workoutId, exerciseId, position }` |
| sets | Array of `{ id, workoutExerciseId, setNumber, weight, reps, isWarmup }` |
| privacyRecords | `{ noticeAcknowledgement: null or { noticeVersion, acknowledgedAt } }` |

`exercises` and `workouts` order by id; blocks by workoutId, position, id; sets by workoutExerciseId, setNumber, id. Preserve stored position/setNumber rather than renumbering. All IDs are account-local export relationships to retained existing database IDs, not cross-account access permissions. Each reference resolves within this file; unused exercises remain present; repeated exercise blocks remain separate.

The field guide must state:

- All account/exercise/workout/acknowledgement timestamps are instants serialized in UTC, not the original input time-zone name. `date` is YYYY-MM-DD, the separately stored local calendar day, never recomputed from startedAt.
- `weight` and `bodyweightKg` are decimal JSON numbers in kilograms. Weight on a bodyweight exercise is added load only. Null weight is not zero; null bodyweight is unrecorded. Preserve numeric values without display rounding.
- Nullable heading text remains null when absent, and stored Unicode text remains unchanged. Null endedAt means unfinished; it is not snapshotAt.
- isWarmup is a boolean, reps is the recorded integer, positions and set numbers define their respective orders. isBodyweight is the exercise's current classification, not historical inferred bodyweight.
- `noticeAcknowledgement` records latest Continue evidence only and is not consent. Null means no retained acknowledgement, not refusal.
- `snapshotAt` identifies the snapshot query boundary; it is not a claim that every row was created then. Empty collections are `[]` and nullable values remain present as null.

Exclude PasswordHash, passwords, TokenVersion, JWTs, secret configuration, derived NormalizedName, other-user records and restricted security control records. A broader access request involving security records gets separate operator review under FR-012, not automatic exclusion by this export contract. No export record/history is persisted. Additional feature-linked personal records cannot be introduced without updating this schema and deletion coverage.

## Concurrency and failure contract

Export reads from one snapshot, but delivery checks fresh authorization on separate connections between bounded chunks. Deletion may take exclusive account access between chunks. After commit no new chunk/write may be authorized; abort the stream and release buffers/snapshot. Expiry/password-change revocation likewise ends access. A partial/truncated JSON response or aborted fetch is failure; UI must not advertise a completed file.

Before snapshot initialization, reconcile/clear the account's retained preparations. Unresolved evidence or unverified cleanup returns a safe 503/contact path rather than an incomplete export. A short shared guard on a separate READ COMMITTED connection covers fresh password/JWT verification, absence of outstanding preparation and snapshot establishment; release it before enumeration. Do not acquire an advisory account lock on the long-running snapshot transaction. Later preparations are outside that snapshot, and subsequent deletion is handled by delivery guards.

Normal notebook writes, login, password changes, acknowledgement and personal response emission participate in the same account protocol. A write already in the shared critical section may finish before deletion obtains exclusive access; deletion removes its committed data. A waiting operation must revalidate and fail after deletion. A password change first invalidates the delete request's old JWT; deletion first makes password change/login fail. Reusing a username never transfers an in-flight operation to the new account.

After password change commits, release exclusive access and validate the newly issued token's version under the response guard, not the old caller version. Concurrent deletion/another change suppresses stale token delivery. The deletion terminal outcome is the explicit non-personal-response exception described above; avoid reentrant conflicting lock acquisition across connections.

Proof requires database tests and real transport/proxy validation. Do not describe a final auth check before a whole unguarded stream as sufficient cancellation.
